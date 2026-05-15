using System.Runtime.InteropServices;
using MetadataDel.Core.Cleaning;
using InteropWord = Microsoft.Office.Interop.Word;

namespace MetadataDel.Core.Word;

/// <summary>
/// Очиститель метаданных для бинарных файлов Word (.doc) через установленный Microsoft Word.
/// </summary>
public sealed class DocCleaner : IFileCleaner
{
    /// <inheritdoc />
    public Task<CleanResult> CleanAsync(string path, CleanOptions options, CancellationToken ct = default)
    {
        var tcs = new TaskCompletionSource<CleanResult>();
        var thread = new Thread(() =>
        {
            InteropWord.Application? app = null;
            InteropWord.Document? doc = null;
            try
            {
                if (options.Backup)
                {
                    File.Copy(path, path + ".bak", overwrite: true);
                }

                app = new InteropWord.Application();
                app.Visible = false;
                app.DisplayAlerts = InteropWord.WdAlertLevel.wdAlertsNone;

                doc = app.Documents.Open(path, ReadOnly: false, Visible: false);
                doc.AcceptAllRevisions();
                foreach (InteropWord.WdRemoveDocInfoType infoType in Enum.GetValues(typeof(InteropWord.WdRemoveDocInfoType)))
                {
                    try { doc.RemoveDocumentInformation(infoType); } catch { }
                }

                doc.Save();
                doc.Close(InteropWord.WdSaveOptions.wdSaveChanges);
                Marshal.FinalReleaseComObject(doc);
                doc = null;

                tcs.TrySetResult(new CleanResult(path, true));
            }
            catch (Exception ex)
            {
                try { File.WriteAllText(path + ".doc-debug.txt", $"{ex.GetType().FullName}: {ex.Message}\n{ex.StackTrace}"); } catch { }
                tcs.TrySetResult(new CleanResult(path, false, ex.Message));
            }
            finally
            {
                if (doc != null)
                {
                    try { doc.Close(InteropWord.WdSaveOptions.wdDoNotSaveChanges); } catch { }
                    try { Marshal.FinalReleaseComObject(doc); } catch { }
                }
                if (app != null)
                {
                    try { app.Quit(InteropWord.WdSaveOptions.wdDoNotSaveChanges); } catch { }
                    try { Marshal.FinalReleaseComObject(app); } catch { }
                }
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        return tcs.Task;
    }
}
