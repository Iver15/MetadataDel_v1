using System.Runtime.InteropServices;
using MetadataDel.Core.Cleaning;
using InteropExcel = Microsoft.Office.Interop.Excel;

namespace MetadataDel.Core.Excel;

/// <summary>
/// Очиститель метаданных для бинарных Excel файлов (.xls) через установленный Microsoft Excel.
/// </summary>
public sealed class XlsCleaner : IFileCleaner
{
    /// <inheritdoc />
    public Task<CleanResult> CleanAsync(string path, CleanOptions options, CancellationToken ct = default)
    {
        var tcs = new TaskCompletionSource<CleanResult>();
        var thread = new Thread(() =>
        {
            InteropExcel.Application? app = null;
            InteropExcel.Workbook? workbook = null;
            try
            {
                if (options.Backup)
                {
                    File.Copy(path, path + ".bak", overwrite: true);
                }

                app = new InteropExcel.Application();
                app.Visible = false;
                app.DisplayAlerts = false;
                app.ScreenUpdating = false;

                workbook = app.Workbooks.Open(path, ReadOnly: false, Notify: false);
                foreach (InteropExcel.XlRemoveDocInfoType infoType in Enum.GetValues(typeof(InteropExcel.XlRemoveDocInfoType)))
                {
                    try { workbook.RemoveDocumentInformation(infoType); } catch { }
                }

                try { workbook.RemovePersonalInformation = true; } catch { }

                workbook.Save();
                workbook.Close(SaveChanges: false);
                Marshal.FinalReleaseComObject(workbook);
                workbook = null;

                tcs.TrySetResult(new CleanResult(path, true));
            }
            catch (Exception ex)
            {
                // Диагностика: пишем ошибку рядом с файлом
                try { File.WriteAllText(path + ".xls-debug.txt", $"{ex.GetType().FullName}: {ex.Message}\n{ex.StackTrace}"); } catch { }
                tcs.TrySetResult(new CleanResult(path, false, ex.Message));
            }
            finally
            {
                if (workbook != null)
                {
                    try { workbook.Close(SaveChanges: false); } catch { }
                    try { Marshal.FinalReleaseComObject(workbook); } catch { }
                }
                if (app != null)
                {
                    try { app.Quit(); } catch { }
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
