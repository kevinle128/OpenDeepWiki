using System.Text;

namespace OpenDeepWiki.Entities;

/// <summary>
/// 仓库来源类型
/// </summary>
public enum RepositorySourceType
{
    Git = 0,
    Archive = 1,
    LocalDirectory = 2
}

/// <summary>
/// 本地目录导入模式
/// </summary>
public enum LocalDirectoryImportMode
{
    Copy = 0,
    Link = 1
}

/// <summary>
/// 解析后的仓库来源信息
/// </summary>
/// <param name="SourceType">来源类型</param>
/// <param name="Location">真实来源位置</param>
public readonly record struct RepositorySourceInfo(RepositorySourceType SourceType, string Location);

/// <summary>
/// 仓库来源编码与解析工具
/// </summary>
public static class RepositorySource
{
    private const string ArchivePrefix = "archive::";
    private const string LocalDirectoryPrefix = "local::";

    public static string EncodeArchivePath(string archivePath)
    {
        return ArchivePrefix + EncodeLocation(archivePath);
    }

    public static string EncodeLocalDirectoryPath(string localPath)
    {
        return LocalDirectoryPrefix + EncodeLocation(localPath);
    }

    public static RepositorySourceInfo Parse(string storedSource)
    {
        if (string.IsNullOrWhiteSpace(storedSource))
        {
            return new RepositorySourceInfo(RepositorySourceType.Git, string.Empty);
        }

        if (storedSource.StartsWith(ArchivePrefix, StringComparison.Ordinal))
        {
            return new RepositorySourceInfo(
                RepositorySourceType.Archive,
                DecodeLocation(storedSource[ArchivePrefix.Length..]));
        }

        if (storedSource.StartsWith(LocalDirectoryPrefix, StringComparison.Ordinal))
        {
            return new RepositorySourceInfo(
                RepositorySourceType.LocalDirectory,
                DecodeLocation(storedSource[LocalDirectoryPrefix.Length..]));
        }

        return new RepositorySourceInfo(RepositorySourceType.Git, storedSource);
    }

    public static bool IsGit(string storedSource)
    {
        return Parse(storedSource).SourceType == RepositorySourceType.Git;
    }

    /// <summary>
    /// Removes the user info (credentials) from a URL with an authority, such as https://user:token@host/path.
    /// Other text, such as a local path or an scp-style address, is returned as it is.
    /// </summary>
    public static string RedactUserInfo(string location)
    {
        if (string.IsNullOrEmpty(location))
        {
            return location;
        }

        var schemeEnd = location.IndexOf("://", StringComparison.Ordinal);
        if (schemeEnd <= 0)
        {
            return location;
        }

        var authorityStart = schemeEnd + 3;
        var authorityEnd = location.IndexOfAny(['/', '?', '#'], authorityStart);
        if (authorityEnd < 0)
        {
            authorityEnd = location.Length;
        }

        var at = location.LastIndexOf('@', authorityEnd - 1, authorityEnd - authorityStart);
        return at < 0 ? location : location[..authorityStart] + location[(at + 1)..];
    }

    private static string EncodeLocation(string location)
    {
        var bytes = Encoding.UTF8.GetBytes(location);
        return Convert.ToBase64String(bytes);
    }

    private static string DecodeLocation(string encodedLocation)
    {
        var bytes = Convert.FromBase64String(encodedLocation);
        return Encoding.UTF8.GetString(bytes);
    }
}
