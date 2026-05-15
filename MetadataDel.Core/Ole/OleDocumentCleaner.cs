using OpenMcdf;
using MetadataDel.Core.Cleaning;

namespace MetadataDel.Core.Ole;

/// <summary>
/// Очиститель метаданных для OLE Compound Document файлов (.doc, .xls)
/// через прямую работу с OLE-структурой. Не требует установленного Office.
/// </summary>
public sealed class OleDocumentCleaner : IFileCleaner
{
    public Task<CleanResult> CleanAsync(string path, CleanOptions options, CancellationToken ct = default)
    {
        try
        {
            if (options.Backup)
            {
                File.Copy(path, path + ".bak", overwrite: true);
            }

            using (var cf = new CompoundFile(path, CFSUpdateMode.Update, CFSConfiguration.Default))
            {
                // Собираем имена всех стримов/сторажей в корне
                var toDelete = new List<string>();
                cf.RootStorage.VisitEntries(item =>
                {
                    // OLE property set потоки начинаются с \x05
                    if (item.Name.StartsWith("\x05"))
                    {
                        toDelete.Add(item.Name);
                    }
                }, recursive: false);

                foreach (var name in toDelete)
                {
                    try { cf.RootStorage.Delete(name); } catch { }
                }

                cf.Commit();
            }

            if (options.WipeFsTimestamps)
            {
                try
                {
                    var ts = new DateTime(1980, 1, 1, 0, 0, 0, DateTimeKind.Utc);
                    File.SetCreationTimeUtc(path, ts);
                    File.SetLastWriteTimeUtc(path, ts);
                    File.SetLastAccessTimeUtc(path, ts);
                }
                catch { }
            }

            return Task.FromResult(new CleanResult(path, true));
        }
        catch (Exception ex)
        {
            return Task.FromResult(new CleanResult(path, false, ex.Message));
        }
    }
}
