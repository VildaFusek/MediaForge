using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace MediaForge.Services;

internal static class RecycleBinService
{
    // IFileOperation runs on an STA thread. RECYCLEONDELETE requires recycling;
    // unlike legacy SHFileOperation it never silently falls back to permanent deletion.
    public static Task RecycleAsync(string path, CancellationToken token)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            object? operationObject = null;
            IShellItem? item = null;
            try
            {
                token.ThrowIfCancellationRequested();
                operationObject = Activator.CreateInstance(Type.GetTypeFromCLSID(
                    new Guid("3AD05575-8857-4850-9277-11B85BDB8E09"), true)!);
                var operation = (IFileOperation)operationObject!;
                // FOF_SILENT | FOF_NOCONFIRMATION | FOF_NOERRORUI |
                // FOFX_RECYCLEONDELETE | FOFX_EARLYFAILURE
                operation.SetOperationFlags(0x0004 | 0x0010 | 0x0400 | 0x00080000 | 0x00100000);
                Guid iid = typeof(IShellItem).GUID;
                Marshal.ThrowExceptionForHR(SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out item));
                operation.DeleteItem(item, IntPtr.Zero);
                token.ThrowIfCancellationRequested();
                operation.PerformOperations();
                operation.GetAnyOperationsAborted(out bool aborted);
                if (aborted || Path.Exists(path))
                    throw new IOException("Položku se nepodařilo přesunout do Koše. Trvalé smazání nebylo použito.");
                completion.SetResult();
            }
            catch (OperationCanceledException) { completion.SetCanceled(token); }
            catch (Exception ex) { completion.SetException(ex); }
            finally
            {
                if (item is not null) Marshal.FinalReleaseComObject(item);
                if (operationObject is not null) Marshal.FinalReleaseComObject(operationObject);
            }
        }) { IsBackground = true, Name = "MediaForge.RecycleBin" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHCreateItemFromParsingName(string path, IntPtr bindContext,
        ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out IShellItem item);

    [ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem { }

    // Method order must match shobjidl_core.h, including unused vtable slots.
    [ComImport, Guid("947AAB5F-0A5C-4C13-B4D6-4BF7836FC9F8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileOperation
    {
        void Advise(IntPtr sink, out uint cookie);
        void Unadvise(uint cookie);
        void SetOperationFlags(uint flags);
        void SetProgressMessage([MarshalAs(UnmanagedType.LPWStr)] string message);
        void SetProgressDialog(IntPtr dialog);
        void SetProperties(IntPtr properties);
        void SetOwnerWindow(uint hwnd);
        void ApplyPropertiesToItem(IShellItem item);
        void ApplyPropertiesToItems([MarshalAs(UnmanagedType.IUnknown)] object items);
        void RenameItem(IShellItem item, [MarshalAs(UnmanagedType.LPWStr)] string name, IntPtr sink);
        void RenameItems([MarshalAs(UnmanagedType.IUnknown)] object items, [MarshalAs(UnmanagedType.LPWStr)] string name);
        void MoveItem(IShellItem item, IShellItem destination, [MarshalAs(UnmanagedType.LPWStr)] string name, IntPtr sink);
        void MoveItems([MarshalAs(UnmanagedType.IUnknown)] object items, IShellItem destination);
        void CopyItem(IShellItem item, IShellItem destination, [MarshalAs(UnmanagedType.LPWStr)] string name, IntPtr sink);
        void CopyItems([MarshalAs(UnmanagedType.IUnknown)] object items, IShellItem destination);
        void DeleteItem(IShellItem item, IntPtr sink);
        void DeleteItems([MarshalAs(UnmanagedType.IUnknown)] object items);
        void NewItem(IShellItem destination, uint attributes, [MarshalAs(UnmanagedType.LPWStr)] string name,
            [MarshalAs(UnmanagedType.LPWStr)] string template, IntPtr sink);
        void PerformOperations();
        void GetAnyOperationsAborted([MarshalAs(UnmanagedType.Bool)] out bool aborted);
    }
}
