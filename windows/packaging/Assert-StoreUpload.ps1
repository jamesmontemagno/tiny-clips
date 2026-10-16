[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $UploadPath
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Reflection.Metadata

function Read-EntryStream([IO.Compression.ZipArchiveEntry] $Entry) {
    $source = $Entry.Open()
    try {
        $buffer = [IO.MemoryStream]::new()
        $source.CopyTo($buffer)
        $buffer.Position = 0
        return ,$buffer
    }
    finally {
        $source.Dispose()
    }
}

function Open-NestedArchive([IO.Compression.ZipArchiveEntry] $Entry) {
    $buffer = Read-EntryStream $Entry
    return [IO.Compression.ZipArchive]::new($buffer, [IO.Compression.ZipArchiveMode]::Read)
}

$upload = [IO.Compression.ZipFile]::OpenRead((Resolve-Path -LiteralPath $UploadPath).Path)
try {
    $symbols = @()
    $requiredAssemblies = @('TinyClips.App', 'TinyClips.Core')
    $symbolEntries = @($upload.Entries | Where-Object Name -like '*.appxsym')
    if ($symbolEntries.Count -eq 0) {
        throw "Store upload is missing .appxsym crash symbols."
    }

    foreach ($entry in $symbolEntries) {
        $archive = Open-NestedArchive $entry
        try {
            foreach ($pdb in $archive.Entries) {
                $name = [IO.Path]::GetFileNameWithoutExtension($pdb.Name)
                if ($pdb.Name -notlike '*.pdb' -or $name -notin $requiredAssemblies) {
                    continue
                }

                $stream = Read-EntryStream $pdb
                $provider = [System.Reflection.Metadata.MetadataReaderProvider]::FromPortablePdbStream($stream)
                try {
                    $reader = $provider.GetMetadataReader()
                    $id = [System.Reflection.Metadata.BlobContentId]::new($reader.DebugMetadataHeader.Id)
                    $symbols += [pscustomobject]@{ Name = $name; Guid = $id.Guid; Stamp = $id.Stamp }
                }
                finally {
                    $provider.Dispose()
                }
            }
        }
        finally {
            $archive.Dispose()
        }
    }

    $bundles = @($upload.Entries | Where-Object Name -like '*.msixbundle')
    if ($bundles.Count -ne 1) {
        throw "Store upload must contain exactly one .msixbundle."
    }

    $bundle = Open-NestedArchive $bundles[0]
    try {
        $architectures = @()
        foreach ($entry in $bundle.Entries | Where-Object Name -like '*.msix') {
            $package = Open-NestedArchive $entry
            try {
                $manifestEntry = $package.GetEntry('AppxManifest.xml')
                if (-not $manifestEntry) {
                    throw "Store bundle package '$($entry.Name)' has no AppxManifest.xml."
                }
                $stream = $manifestEntry.Open()
                try {
                    $manifest = [xml]::new()
                    $manifest.Load($stream)
                }
                finally {
                    $stream.Dispose()
                }

                if ($manifest.Package.Identity.ResourceId) {
                    continue
                }
                $architecture = [string] $manifest.Package.Identity.ProcessorArchitecture
                if ($architecture -notin @('x64', 'arm64')) {
                    throw "Unexpected Store package architecture '$architecture'."
                }

                foreach ($name in $requiredAssemblies) {
                    $assembly = $package.GetEntry("$name.dll")
                    if (-not $assembly) {
                        throw "Store $architecture package is missing '$name.dll'."
                    }

                    $stream = Read-EntryStream $assembly
                    $pe = [System.Reflection.PortableExecutable.PEReader]::new($stream)
                    try {
                        $matched = $false
                        foreach ($debugEntry in $pe.ReadDebugDirectory()) {
                            if ($debugEntry.Type -ne [System.Reflection.PortableExecutable.DebugDirectoryEntryType]::CodeView) {
                                continue
                            }
                            $codeView = $pe.ReadCodeViewDebugDirectoryData($debugEntry)
                            $matchingSymbols = @($symbols | Where-Object {
                                $_.Name -eq $name -and $_.Guid -eq $codeView.Guid -and $_.Stamp -eq $debugEntry.Stamp
                            })
                            if ($matchingSymbols.Count -gt 0) {
                                $matched = $true
                                break
                            }
                        }
                        if (-not $matched) {
                            throw "Store $architecture package is missing matching '$name.pdb' crash symbols."
                        }
                        Write-Output "Verified Store $architecture crash symbols: $name.pdb"
                    }
                    finally {
                        $pe.Dispose()
                    }
                }
                $architectures += $architecture
            }
            finally {
                $package.Dispose()
            }
        }

        foreach ($architecture in @('x64', 'arm64')) {
            if ($architecture -notin $architectures) {
                throw "Store upload is missing the $architecture application package."
            }
        }
    }
    finally {
        $bundle.Dispose()
    }
}
finally {
    $upload.Dispose()
}
