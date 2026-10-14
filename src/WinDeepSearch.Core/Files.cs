namespace WinDeepSearch.Core;

internal static class Files
{
    const FileAttributes RecallOnOpen = (FileAttributes)0x00040000;
    const FileAttributes RecallOnDataAccess = (FileAttributes)0x00400000;

    /// <summary>Opens for reading without blocking other programs that are writing or deleting the file.</summary>
    public static FileStream OpenRead(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16, FileOptions.SequentialScan);

    /// <summary>True for cloud files (OneDrive and similar) whose content isn't on this PC; reading them would download them.</summary>
    public static bool IsOnlineOnly(FileAttributes attributes) =>
        (attributes & (FileAttributes.Offline | RecallOnOpen | RecallOnDataAccess)) != 0;
}
