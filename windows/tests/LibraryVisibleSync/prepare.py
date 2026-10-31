"""Extract the real before/after method; do not maintain algorithm imitations."""
import hashlib
import json
import pathlib
import subprocess

here = pathlib.Path(__file__).resolve().parent
repo = here.parents[2]
base = "fa833d7e2e67493237f56d0c29a4bd14e63dd72e"
path = "windows/src/TinyClips.App/ViewModels/ClipsLibrary/ClipsLibraryViewModel.cs"
before = subprocess.check_output(["git", "-C", str(repo), "show", f"{base}:{path}"])
after = (repo / path).read_bytes()
out = here / "obj"
out.mkdir(exist_ok=True)
manifest = {"baseline": base, "method": "SyncVisibleClips", "sources": {}, "excerpts": {}}
for name, source in (("Before", before), ("After", after)):
    text = source.decode()
    start = text.index("    private void SyncVisibleClips(")
    end = text.index("    private void RebuildSidebar()", start)
    method = text[start:end]
    generated = "using System.Collections.ObjectModel;\n" + f"internal sealed class {name} : Host\n{{\n"
    generated += method.replace("private void SyncVisibleClips(", "public override void SyncVisibleClips(", 1) + "}\n"
    (out / f"{name}.cs").write_text(generated)
    manifest["sources"][name] = hashlib.sha256(source).hexdigest()
    manifest["excerpts"][name] = hashlib.sha256(method.encode()).hexdigest()
    manifest.setdefault("generated", {})[name] = hashlib.sha256(generated.encode()).hexdigest()

# Require unchanged fallback and callers; only the early-return prefix may differ.
def parts(source):
    text = source.decode()
    start = text.index("    private void SyncVisibleClips(")
    fallback = text.index("        for (var i = Clips.Count - 1;", start)
    end = text.index("    private void RebuildSidebar()", start)
    return text[:start], text[fallback:end], text[end:]

assert parts(before) == parts(after), "Changes extend beyond the no-op guard"
item = repo / "windows/src/TinyClips.App/ViewModels/ClipsLibrary/ClipItemViewModel.cs"
manifest["itemSourceSha256"] = hashlib.sha256(item.read_bytes()).hexdigest()
manifest["harness"] = {p: hashlib.sha256((here / p).read_bytes()).hexdigest()
                       for p in ("Program.cs", "LibraryVisibleSync.csproj", "prepare.py")}
manifest["scope"] = "Verbatim method, visibility/override only; reference-equality UI-free item and host"
(out / "source-manifest.json").write_text(json.dumps(manifest, indent=2) + "\n")
print("Extracted pinned baseline/current methods; fallback and all surrounding VM source identical.")
