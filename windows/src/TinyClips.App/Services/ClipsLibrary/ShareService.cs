using System.Diagnostics;
using System.Runtime.InteropServices;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;

namespace TinyClips.App.Services.ClipsLibrary;

/// <summary>
/// Windows Share contract + drag-out payloads for clips. Share requires the window's HWND via the
/// <c>IDataTransferManagerInterop</c> COM interface on WinUI 3 desktop.
/// </summary>
internal static class ShareService
{
    private static readonly Guid DataTransferManagerIid = new("A5CAEE9B-8708-49D1-8D36-67D25A8DA00C");

    public static void Share(nint hwnd, IReadOnlyList<string> paths, string title)
    {
        if (paths.Count == 0)
        {
            return;
        }

        try
        {
            var interop = DataTransferManager.As<IDataTransferManagerInterop>();
            var iid = DataTransferManagerIid;
            var manager = DataTransferManager.FromAbi(interop.GetForWindow(hwnd, ref iid));

            async void OnDataRequested(DataTransferManager sender, DataRequestedEventArgs args)
            {
                sender.DataRequested -= OnDataRequested;
                var deferral = args.Request.GetDeferral();
                try
                {
                    args.Request.Data.Properties.Title = title;
                    args.Request.Data.Properties.Description = paths.Count == 1
                        ? Path.GetFileName(paths[0])
                        : $"{paths.Count} clips from Tiny Clips";
                    var files = new List<IStorageItem>(paths.Count);
                    foreach (var path in paths)
                    {
                        files.Add(await StorageFile.GetFileFromPathAsync(path));
                    }

                    args.Request.Data.SetStorageItems(files, readOnly: true);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"ShareService: DataRequested failed: {ex}");
                    args.Request.FailWithDisplayText("Couldn't prepare these clips for sharing.");
                }
                finally
                {
                    deferral.Complete();
                }
            }

            manager.DataRequested += OnDataRequested;
            interop.ShowShareUIForWindow(hwnd);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"ShareService: share failed: {ex}");
        }
    }

    /// <summary>
    /// Populates a drag <see cref="DataPackage"/> with the clip files so they can be dropped into
    /// Explorer, Teams, browsers, etc. Uses a deferred provider because file lookup is async.
    /// </summary>
    public static void PopulateDragPackage(DataPackage package, IReadOnlyList<string> paths)
    {
        if (paths.Count == 0)
        {
            return;
        }

        package.RequestedOperation = DataPackageOperation.Copy;
        package.SetDataProvider(StandardDataFormats.StorageItems, async request =>
        {
            var deferral = request.GetDeferral();
            try
            {
                var files = new List<IStorageItem>(paths.Count);
                foreach (var path in paths)
                {
                    files.Add(await StorageFile.GetFileFromPathAsync(path));
                }

                request.SetData(files);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"ShareService: drag provider failed: {ex}");
            }
            finally
            {
                deferral.Complete();
            }
        });
    }

    [ComImport]
    [Guid("3A3DCD6C-3EAB-43DC-BCDE-45671CE800C8")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDataTransferManagerInterop
    {
        nint GetForWindow([In] nint appWindow, [In] ref Guid riid);

        void ShowShareUIForWindow(nint appWindow);
    }
}
