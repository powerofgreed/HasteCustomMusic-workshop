using System;
using System.IO;
using UnityEngine;

public static class YouTubeBatchGenerator
{
    private static readonly string[] BatFiles = new[]
    {
        "setup_youtube_tools_user.bat",
        "download_youtube_mp3.bat"
    };

    public static bool RecreateBatFiles(out string message)
    {
        try
        {
            string baseDir = LandfallConfig.ConfigDirectory;
            foreach (var bat in BatFiles)
            {
                string batPath = Path.Combine(baseDir, bat);
                if (File.Exists(batPath))
                {
                    File.Delete(batPath);
                }
            }

            File.WriteAllText(Path.Combine(baseDir, "download_youtube_mp3.bat"), GetDownloadBatchContent());
            File.WriteAllText(Path.Combine(baseDir, "setup_youtube_tools_user.bat"), GetSetupToolsBatchContent());

            message = "Batch files recreated successfully.";
            return true;
        }
        catch (Exception ex)
        {
            message = $"Failed to recreate batch files: {ex.Message}";
            return false;
        }
    }

    private static string GetDownloadBatchContent()
    {
        string embedThumbnailFlag = LandfallConfig.CurrentConfig.YoutubeEmbedThumbnail ? " --embed-thumbnail" : string.Empty;
        string cookiesFlag = LandfallConfig.CurrentConfig.YoutubeUseCookies ? " --cookies \"%~dp0cookies.txt\"" : string.Empty;

        return "@echo off\r\n" +
               "setlocal enabledelayedexpansion\r\n" +
               "set \"YTDLP=%~dp0yt-dlp\\yt-dlp.exe\"\r\n" +
               "set \"FFMPEG_DIR=%~dp0ffmpeg\\ffmpeg-master-latest-win64-gpl\\bin\"\r\n" +
               "set \"DEST=%~dp0MusicHere\"\r\n" +
               "if not exist \"%YTDLP%\" (\r\n" +
               "    echo Error: yt-dlp.exe not found in yt-dlp\\ folder.\r\n" +
               "    pause\r\n" +
               "    exit /b 1\r\n" +
               ")\r\n" +
               "if not exist \"%FFMPEG_DIR%\\ffmpeg.exe\" (\r\n" +
               "    echo Error: ffmpeg.exe not found in ffmpeg\\ffmpeg-master-latest-win64-gpl\\bin.\r\n" +
               "    pause\r\n" +
               "    exit /b 1\r\n" +
               ")\r\n" +
               "if not exist \"%DEST%\" mkdir \"%DEST%\" 2>nul\r\n" +
               (LandfallConfig.CurrentConfig.YoutubeUseCookies
                   ? "if not exist \"%~dp0cookies.txt\" echo.> \"%~dp0cookies.txt\"\r\n"
                   : string.Empty) +
               "echo.\r\n" +
               "echo HasteCustomMusic - YouTube to MP3\r\n" +
               "echo.\r\n" +
               "set /p \"input_url=Enter YouTube URL (video or playlist): \"\r\n" +
               "if \"%input_url%\"==\"\" (\r\n" +
               "    echo No URL provided. Exiting.\r\n" +
               "    pause\r\n" +
               "    exit /b 0\r\n" +
               ")\r\n" +
               "\"%YTDLP%\" --update --ffmpeg-location \"%FFMPEG_DIR%\" --ignore-errors --extract-audio --extractor-args \"youtube:player_client=default,web_safari\" --audio-format mp3 --audio-quality 0 --add-metadata" + embedThumbnailFlag + cookiesFlag + " --output \"%DEST%\\%%(playlist_index|)s%%(playlist_index|- )s%%(title)s.%%(ext)s\" \"%input_url%\"\r\n" +
               "echo.\r\n" +
               "echo Download complete!\r\n" +
               "pause\r\n" +
               "endlocal\r\n";
    }

    private static string GetSetupToolsBatchContent()
    {
        return "@echo off\r\n" +
               "REM ============================================================================\r\n" +
               "REM HasteCustomMusic YouTube Tools Downloader - USER-LOCAL INSTALLER\r\n" +
               "REM ============================================================================\r\n" +
               "REM This script downloads yt-dlp and ffmpeg into %APPDATA%\\HasteCustomMusic\\youtube_tools\r\n" +
               "REM It does NOT change any permissions. After completion it opens the folder\r\n" +
               "REM so the user can manually copy/move it into the game folder if desired.\r\n" +
               "REM ============================================================================\r\n" +
               "setlocal enabledelayedexpansion\r\n" +
               "echo.\r\n" +
               "echo ============================================================================\r\n" +
               "echo  HasteCustomMusic - User-local YouTube Tools Installer\r\n" +
               "echo ============================================================================\r\n" +
               "echo.\r\n" +
               "echo This installer will create the folder in your Roaming AppData and place\r\n" +
               "echo yt-dlp and ffmpeg there.\r\n" +
               "echo.\r\n" +
               "echo IMPORTANT: After this script finishes you will need to manually copy or\r\n" +
               "echo move the created folder (in your AppData) into the game installation\r\n" +
               "echo folder where the mod resides. The script will open the folder at the end\r\n" +
               "echo to make this easy.\r\n" +
               "echo.\r\n" +
               "choice /M \"Continue and download tools to %APPDATA%\\HasteCustomMusic\\youtube_tools?\"\r\n" +
               "if errorlevel 2 (\r\n" +
               "    echo Aborted by user.\r\n" +
               "    exit /b 1\r\n" +
               ")\r\n" +
               "REM === Variables ===\r\n" +
               "set \"BASE=%APPDATA%\\HasteCustomMusic\\youtube_tools\"\r\n" +
               "set \"YT_DLP_DIR=%BASE%\\yt-dlp\"\r\n" +
               "set \"FFMPEG_DIR=%BASE%\\ffmpeg\"\r\n" +
               "set \"TEMP_ZIP=%TEMP%\\ffmpeg_appdata_download.zip\"\r\n" +
               "set \"LOGFILE=%BASE%\\setup_youtube_tools_appdata.log\"\r\n" +
               "echo Creating folders under %BASE% ...\r\n" +
               "mkdir \"%YT_DLP_DIR%\" 2>nul\r\n" +
               "mkdir \"%FFMPEG_DIR%\" 2>nul\r\n" +
               "if not exist \"%BASE%\" (\r\n" +
               "    echo ERROR: Could not create %BASE%. Check your user profile.\r\n" +
               "    explorer \"%BASE%\"\r\n" +
               "    pause\r\n" +
               "    exit /b 1\r\n" +
               ")\r\n" +
               "if exist \"%LOGFILE%\" del \"%LOGFILE%\" >nul 2>&1\r\n" +
               "echo %DATE% %TIME% - Starting installation > \"%LOGFILE%\"\r\n" +
               "echo [1/4] Installing Deno...\r\n" +
               "echo Deno is required for improved JavaScript compatibility with yt-dlp.\r\n" +
               "powershell -NoProfile -Command \"try { irm https://deno.land/install.ps1 | iex } catch { Write-Host 'WARNING: Deno installation failed or Deno is already installed. Continuing...'; exit 0 }\"\r\n" +
               "echo %DATE% %TIME% - Deno installation attempted >> \"%LOGFILE%\"\r\n" +
               "echo [2/4] Downloading yt-dlp...\r\n" +
               "echo Source: https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp.exe\r\n" +
               "powershell -NoProfile -Command \"try { Invoke-WebRequest -Uri 'https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp.exe' -OutFile '%YT_DLP_DIR%\\yt-dlp.exe' -TimeoutSec 90 } catch { Write-Host 'ERROR:' $_.Exception.Message; exit 1 }\"\r\n" +
               "if errorlevel 1 (\r\n" +
               "    echo ERROR: Failed to download yt-dlp. See %LOGFILE%\r\n" +
               "    echo %DATE% %TIME% - ERROR: yt-dlp download failed >> \"%LOGFILE%\"\r\n" +
               "    explorer \"%BASE%\"\r\n" +
               "    pause\r\n" +
               "    exit /b 1\r\n" +
               ")\r\n" +
               "if not exist \"%YT_DLP_DIR%\\yt-dlp.exe\" (\r\n" +
               "    echo ERROR: yt-dlp.exe missing after download\r\n" +
               "    echo %DATE% %TIME% - ERROR: yt-dlp missing >> \"%LOGFILE%\"\r\n" +
               "    explorer \"%BASE%\"\r\n" +
               "    pause\r\n" +
               "    exit /b 1\r\n" +
               ")\r\n" +
               "echo ✓ yt-dlp downloaded >> \"%LOGFILE%\"\r\n" +
               "echo [3/4] Downloading ffmpeg ZIP...\r\n" +
               "echo Source: https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-master-latest-win64-gpl.zip\r\n" +
               "if exist \"%TEMP_ZIP%\" del /Q \"%TEMP_ZIP%\" >nul 2>&1\r\n" +
               "powershell -NoProfile -Command \"try { Invoke-WebRequest -Uri 'https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-master-latest-win64-gpl.zip' -OutFile '%TEMP_ZIP%' -TimeoutSec 180 } catch { Write-Host 'ERROR:' $_.Exception.Message; exit 1 }\"\r\n" +
               "if errorlevel 1 (\r\n" +
               "    echo ERROR: ffmpeg download failed. See %LOGFILE%\r\n" +
               "    echo %DATE% %TIME% - ERROR: ffmpeg download failed >> \"%LOGFILE%\"\r\n" +
               "    explorer \"%BASE%\"\r\n" +
               "    pause\r\n" +
               "    exit /b 1\r\n" +
               ")\r\n" +
               "if not exist \"%TEMP_ZIP%\" (\r\n" +
               "    echo ERROR: ffmpeg ZIP missing after download\r\n" +
               "    echo %DATE% %TIME% - ERROR: ffmpeg ZIP missing >> \"%LOGFILE%\"\r\n" +
               "    explorer \"%BASE%\"\r\n" +
               "    pause\r\n" +
               "    exit /b 1\r\n" +
               ")\r\n" +
               "echo ✓ ffmpeg ZIP downloaded >> \"%LOGFILE%\"\r\n" +
               "echo [4/4] Extracting ffmpeg into %FFMPEG_DIR% ...\r\n" +
               "powershell -NoProfile -Command \"try { Expand-Archive -Path '%TEMP_ZIP%' -DestinationPath '%FFMPEG_DIR%' -Force } catch { Write-Host 'ERROR:' $_.Exception.Message; exit 1 }\"\r\n" +
               "if errorlevel 1 (\r\n" +
               "    echo ERROR: ffmpeg extraction failed. See %LOGFILE%\r\n" +
               "    echo %DATE% %TIME% - ERROR: ffmpeg extraction failed >> \"%LOGFILE%\"\r\n" +
               "    explorer \"%BASE%\"\r\n" +
               "    pause\r\n" +
               "    exit /b 1\r\n" +
               ")\r\n" +
               "if exist \"%TEMP_ZIP%\" del /Q \"%TEMP_ZIP%\" >nul 2>&1\r\n" +
               "echo Verifying installation...\r\n" +
               "if not exist \"%YT_DLP_DIR%\\yt-dlp.exe\" (\r\n" +
               "    echo ERROR: yt-dlp missing after installation >> \"%LOGFILE%\"\r\n" +
               "    echo ERROR: yt-dlp missing after installation\r\n" +
               "    explorer \"%BASE%\"\r\n" +
               "    pause\r\n" +
               "    exit /b 1\r\n" +
               ")\r\n" +
               "set \"FOUND_FFMPEG=\"\r\n" +
               "for /f \"delims=\" %%F in ('dir /b /s \"%FFMPEG_DIR%\\ffmpeg.exe\" 2^>nul') do set \"FOUND_FFMPEG=%%F\"\r\n" +
               "if not defined FOUND_FFMPEG (\r\n" +
               "    echo WARNING: ffmpeg.exe not found in extracted files >> \"%LOGFILE%\"\r\n" +
               "    echo WARNING: ffmpeg.exe not found in extracted files\r\n" +
               ")\r\n" +
               "echo Installation complete.\r\n" +
               "echo %DATE% %TIME% - Installation complete >> \"%LOGFILE%\"\r\n" +
               "echo.\r\n" +
               "echo IMPORTANT — manual copy required:\r\n" +
               "echo A folder containing the tools has been created and opened in Explorer.\r\n" +
               "echo Please manually copy or move the opened folder into the game folder where the mod is installed (for example, the folder that contains the mod's .bat files).\r\n" +
               "echo This avoids any permission changes inside Program Files.\r\n" +
               "echo.\r\n" +
               "explorer \"%BASE%\"\r\n" +
               "echo.\r\n" +
               "pause\r\n" +
               "endlocal\r\n";
    }
}
