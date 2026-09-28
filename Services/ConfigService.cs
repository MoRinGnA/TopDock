using System;
using System.IO;
using System.Text.Json;
using TopDock.Models;

namespace TopDock.Services
{
    public static class ConfigService
    {
        private static readonly string ConfigDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TopDock");
        private static readonly string ConfigPath = Path.Combine(ConfigDir, "config.json");

        // 이전 버전이 설치 폴더에 쓰던 구 config 경로 (마이그레이션용)
        private static readonly string LegacyConfigPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config.json");
        // 구 프로그램명(WinNotch) 시절 설정 경로 → TopDock으로 이름 바꾼 뒤 1회 이관
        private static readonly string WinNotchConfigDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WinNotch");
        private static readonly string WinNotchConfigPath = Path.Combine(WinNotchConfigDir, "config.json");

        private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

        public static Config Current { get; private set; } = new();

        public static void Load()
        {
            try
            {
                Directory.CreateDirectory(ConfigDir);

                // 구버전 설치폴더 config가 있으면 새 위치로 이관 (API 키 등 보존)
                if (!File.Exists(ConfigPath) && File.Exists(LegacyConfigPath))
                {
                    File.Copy(LegacyConfigPath, ConfigPath);
                }

                // WinNotch 시절 %APPDATA% 설정도 이관
                if (!File.Exists(ConfigPath) && File.Exists(WinNotchConfigPath))
                {
                    File.Copy(WinNotchConfigPath, ConfigPath);
                }

                // 가사 디스크 캐시도 TopDock 폴더로 이관
                string winNotchLyricsDir = Path.Combine(WinNotchConfigDir, "LyricsCache");
                string lyricsDir = Path.Combine(ConfigDir, "LyricsCache");
                if (Directory.Exists(winNotchLyricsDir) && !Directory.Exists(lyricsDir))
                {
                    Directory.Move(winNotchLyricsDir, lyricsDir);
                }

                if (File.Exists(ConfigPath))
                {
                    string json = File.ReadAllText(ConfigPath);
                    var config = JsonSerializer.Deserialize<Config>(json);
                    if (config != null)
                    {
                        Current = config;
                        return;
                    }
                }

                Save(); // 기본 설정 파일 생성
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error loading config: {ex.Message}");
            }
        }

        public static void Save()
        {
            try
            {
                Directory.CreateDirectory(ConfigDir);
                string json = JsonSerializer.Serialize(Current, WriteOptions);
                File.WriteAllText(ConfigPath, json);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error saving config: {ex.Message}");
            }
        }
    }
}
