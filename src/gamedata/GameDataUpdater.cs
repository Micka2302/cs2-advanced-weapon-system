using System.Security.Cryptography;
using Microsoft.Extensions.Logging;

namespace AdvancedWeaponSystem;

public static class GameDataUpdater
{
    private const string GameDataFileName = "gamedata.json";

    public static async Task<bool> DownloadMissingFile(string gameDataDirectory, string updateUrl, ILogger logger)
    {
        if (string.IsNullOrWhiteSpace(updateUrl))
        {
            logger.LogWarning("Gamedata auto-update is enabled, but GameDataUpdateUrl is empty.");
            return false;
        }

        string gameDataPath = Path.Combine(gameDataDirectory, GameDataFileName);
        return await CheckAndDownloadFile(gameDataPath, updateUrl.Trim(), logger);
    }

    private static async Task<bool> CheckAndDownloadFile(string filePath, string url, ILogger logger)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);

        if (!File.Exists(filePath))
        {
            bool downloaded = await DownloadFileFromGithub(url, filePath, logger);
            if (downloaded)
                logger.LogInformation("Downloaded missing gamedata file to {GameDataPath}.", filePath);

            return downloaded;
        }

        bool isFileDifferent = await IsFileDifferent(filePath, url, logger);
        if (!isFileDifferent)
            return false;

        bool updated = await DownloadFileFromGithub(url, filePath, logger);
        if (updated)
            logger.LogInformation("Updated gamedata file from {UpdateUrl}.", url);

        return updated;
    }

    private static async Task<bool> IsFileDifferent(string localFilePath, string url, ILogger logger)
    {
        try
        {
            byte[] localFileBytes = await File.ReadAllBytesAsync(localFilePath);
            string localFileHash = GetFileHash(localFileBytes);

            using HttpClient client = CreateHttpClient();
            byte[] remoteFileBytes = await client.GetByteArrayAsync(url);
            string remoteFileHash = GetFileHash(remoteFileBytes);
            return localFileHash != remoteFileHash;
        }
        catch (Exception ex)
        {
            logger.LogWarning("Could not compare gamedata with remote source: {Message}", ex.Message);
            return false;
        }
    }

    private static async Task<bool> DownloadFileFromGithub(string url, string destinationPath, ILogger logger)
    {
        try
        {
            using HttpClient client = CreateHttpClient();
            byte[] fileBytes = await client.GetByteArrayAsync(url);
            await File.WriteAllBytesAsync(destinationPath, fileBytes);
            return true;
        }
        catch (Exception ex)
        {
            logger.LogWarning("Could not download gamedata from {UpdateUrl}: {Message}", url, ex.Message);
            return false;
        }
    }

    private static HttpClient CreateHttpClient()
    {
        return new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(6)
        };
    }

    private static string GetFileHash(byte[] fileBytes)
    {
        byte[] hashBytes = MD5.HashData(fileBytes);
        return BitConverter.ToString(hashBytes).Replace("-", "").ToLowerInvariant();
    }
}
