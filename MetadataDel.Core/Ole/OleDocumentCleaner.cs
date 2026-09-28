using OpenMcdf;
using MetadataDel.Core.Cleaning;

namespace MetadataDel.Core.Ole;

/// <summary>Очищает корневые OLE property streams без установленного Office.</summary>
public sealed class OleDocumentCleaner : IFileCleaner
{
    /// <inheritdoc />
    public Task<CleanResult> CleanAsync(string path, CleanOptions options, CancellationToken ct = default)
    {
        try
        {
            using var transaction = new FileCleaningTransaction(path, options, ct);
            var rebuilt = transaction.WorkingPath + ".rebuilt";
            try
            {
                // Copy live entries into a fresh container; free sectors must not retain deleted properties.
                using (var source = RootStorage.OpenRead(transaction.WorkingPath))
                {
                    source.BaseStream.Position = 26;
                    var version = (OpenMcdf.Version)source.BaseStream.ReadByte();
                    using var destination = RootStorage.Create(rebuilt, version);
                    CopyStorage(source, destination, removeProperties: true, ct);
                }
                File.Move(rebuilt, transaction.WorkingPath, overwrite: true);
                transaction.Commit(ct);
            }
            finally { if (File.Exists(rebuilt)) File.Delete(rebuilt); }
            return Task.FromResult(new CleanResult(path, true,
                FileCleaningTransaction.WipeTimestamps(path, options.WipeFsTimestamps)));
        }
        catch (Exception ex)
        {
            return Task.FromResult(new CleanResult(path, false, ex.Message));
        }
    }

    private static void CopyStorage(Storage source, Storage destination, bool removeProperties, CancellationToken ct)
    {
        destination.CLSID = source.CLSID;
        destination.StateBits = source.StateBits;
        foreach (var entry in source.EnumerateEntries())
        {
            ct.ThrowIfCancellationRequested();
            if (removeProperties && entry.Name.StartsWith("\x05", StringComparison.Ordinal)) continue;
            if (entry.Type == EntryType.Storage)
                CopyStorage(source.OpenStorage(entry.Name), destination.CreateStorage(entry.Name), false, ct);
            else if (entry.Type == EntryType.Stream)
            {
                using var input = source.OpenStream(entry.Name);
                using var output = destination.CreateStream(entry.Name);
                input.CopyTo(output);
            }
        }
    }
}
