using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;

namespace IWar3Key.Shell.Core
{
    public sealed class VersionProfile
    {
        public string Id { get; set; }
        public string DisplayName { get; set; }
        public string[] VersionPrefixes { get; set; }
        public long CguiGlobalRva { get; set; }
        public int ChatBarOffset { get; set; }
        public int TextObjectOffset { get; set; }
        public int TextBufferOffset { get; set; }
        public long IsChattingRva { get; set; }
        // CGameUI field holding the CEscMenu object pointer. 0 means the
        // build has no verified EscMenu detection; menu-key protection is
        // then unavailable.
        public int EscapeMenuOffset { get; set; }
        public long UnitListRva { get; set; }
        public int SelectionOffset { get; set; }
        public int ActiveUnitOffset { get; set; }
        public long CommandBarOffset { get; set; }
        public long CommandGridOffset { get; set; }
        public long CommandFrameButtonOffset { get; set; }
        public long CommandButtonHotkeyOffset { get; set; }
        public long CameraSetFieldRva { get; set; }
        public bool StatusBarSupported { get; set; }
        public long StatusBarAllocCallRva { get; set; }
        public long StatusBarUpdateCallRva { get; set; }
        public long StatusBarOriginalUpdateRva { get; set; }
        public long StatusBarCtorRva { get; set; }
        public long StatusBarGetUnitStateRva { get; set; }
        public long StatusBarUnitScaleRva { get; set; }
        public long StatusBarSetBarPositionRva { get; set; }
        public long StatusBarSetBarBoundsRva { get; set; }
        public long StatusBarGetUnitUIDefRva { get; set; }
        public long StatusBarIsUnitStructureRva { get; set; }
        public long StatusBarGetLocalPlayerRva { get; set; }
        public long StatusBarGetCPlayerWar3Rva { get; set; }
        public long StatusBarCPlayerAttrRva { get; set; }
        public long StatusBarIntegerGetValueRva { get; set; }
        public int StatusBarHealthFlagOffset { get; set; }
        public bool NativeChat { get; set; }
        public bool SkillConvert { get; set; }
        public bool CameraDistance { get; set; }

        public bool Matches(string versionText)
        {
            if (string.IsNullOrWhiteSpace(versionText) || VersionPrefixes == null)
            {
                return false;
            }

            return VersionPrefixes.Any(prefix =>
                !string.IsNullOrWhiteSpace(prefix) &&
                versionText.Trim().StartsWith(prefix.Trim(), StringComparison.OrdinalIgnoreCase));
        }
    }

    public sealed class ProfileCatalog
    {
        private readonly List<VersionProfile> _profiles = new List<VersionProfile>();

        public IReadOnlyList<VersionProfile> Profiles => _profiles;

        public void Load(string directory, Logger log)
        {
            _profiles.Clear();

            try
            {
                if (Directory.Exists(directory))
                {
                    foreach (string file in Directory.GetFiles(directory, "*.json").OrderBy(x => x))
                    {
                        try
                        {
                            var dto = new JavaScriptSerializer().Deserialize<ProfileDto>(File.ReadAllText(file, Encoding.UTF8));
                            if (dto == null || string.IsNullOrWhiteSpace(dto.Id))
                            {
                                continue;
                            }

                            _profiles.Add(Convert(dto));
                        }
                        catch (Exception ex)
                        {
                            log.Error("读取版本档案失败: " + Path.GetFileName(file), ex);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                log.Error("扫描版本档案目录失败", ex);
            }

            if (_profiles.Count == 0)
            {
                log.Warn("未找到版本档案，使用内置 1.24e/1.20e/1.27a 档案");
                _profiles.AddRange(BuiltInProfiles());
            }

            log.Info("已加载版本档案: " + string.Join(", ", _profiles.Select(p => p.Id)));
        }

        public VersionProfile Match(string versionText)
        {
            if (string.IsNullOrWhiteSpace(versionText))
            {
                return null;
            }

            return _profiles
                .Where(p => p.Matches(versionText))
                .OrderByDescending(p => p.VersionPrefixes.Max(x => x.Length))
                .FirstOrDefault();
        }

        private static VersionProfile Convert(ProfileDto dto)
        {
            var profile = new VersionProfile
            {
                Id = dto.Id,
                DisplayName = string.IsNullOrWhiteSpace(dto.DisplayName) ? dto.Id : dto.DisplayName,
                VersionPrefixes = dto.VersionPrefixes ?? new string[0],
                CguiGlobalRva = ParseNumber(dto.Chat?.CguiGlobalRva),
                ChatBarOffset = (int)ParseNumber(dto.Chat?.ChatBarOffset),
                TextObjectOffset = (int)ParseNumber(dto.Chat?.TextObjectOffset),
                TextBufferOffset = (int)ParseNumber(dto.Chat?.TextBufferOffset),
                IsChattingRva = ParseNumber(dto.Chat?.IsChattingRva),
                EscapeMenuOffset = (int)ParseNumber(dto.Ui?.EscapeMenuOffset),
                UnitListRva = ParseNumber(dto.Skills?.UnitListRva),
                SelectionOffset = (int)ParseNumber(dto.Skills?.SelectionOffset),
                ActiveUnitOffset = (int)ParseNumber(dto.Skills?.ActiveUnitOffset),
                CommandBarOffset = ParseNumber(dto.Skills?.CommandBarOffset),
                CommandGridOffset = ParseNumber(dto.Skills?.CommandGridOffset),
                CommandFrameButtonOffset = ParseNumber(dto.Skills?.CommandFrameButtonOffset),
                CommandButtonHotkeyOffset = ParseNumber(dto.Skills?.CommandButtonHotkeyOffset),
                CameraSetFieldRva = ParseNumber(dto.Camera?.SetFieldRva),
                StatusBarSupported = dto.StatusBars?.Supported ?? false,
                StatusBarAllocCallRva = ParseNumber(dto.StatusBars?.AllocCallRva),
                StatusBarUpdateCallRva = ParseNumber(dto.StatusBars?.UpdateCallRva),
                StatusBarOriginalUpdateRva = ParseNumber(dto.StatusBars?.OriginalUpdateRva),
                StatusBarCtorRva = ParseNumber(dto.StatusBars?.CtorRva),
                StatusBarGetUnitStateRva = ParseNumber(dto.StatusBars?.GetUnitStateRva),
                StatusBarUnitScaleRva = ParseNumber(dto.StatusBars?.UnitScaleRva),
                StatusBarSetBarPositionRva = ParseNumber(dto.StatusBars?.SetBarPositionRva),
                StatusBarSetBarBoundsRva = ParseNumber(dto.StatusBars?.SetBarBoundsRva),
                StatusBarGetUnitUIDefRva = ParseNumber(dto.StatusBars?.GetUnitUIDefRva),
                StatusBarIsUnitStructureRva = ParseNumber(dto.StatusBars?.IsUnitStructureRva),
                StatusBarGetLocalPlayerRva = ParseNumber(dto.StatusBars?.GetLocalPlayerRva),
                StatusBarGetCPlayerWar3Rva = ParseNumber(dto.StatusBars?.GetCPlayerWar3Rva),
                StatusBarCPlayerAttrRva = ParseNumber(dto.StatusBars?.CPlayerAttrRva),
                StatusBarIntegerGetValueRva = ParseNumber(dto.StatusBars?.IntegerGetValueRva),
                StatusBarHealthFlagOffset = (int)ParseNumber(dto.StatusBars?.HealthFlagOffset),
                NativeChat = dto.Capabilities?.NativeChat ?? false,
                SkillConvert = dto.Capabilities?.SkillConvert ?? false,
                CameraDistance = dto.Capabilities?.CameraDistance ?? false
            };

            return profile;
        }

        private static long ParseNumber(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return 0;
            }

            string value = text.Trim();
            try
            {
                if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                {
                    return System.Convert.ToInt64(value.Substring(2), 16);
                }

                return System.Convert.ToInt64(value);
            }
            catch
            {
                return 0;
            }
        }

        private static IEnumerable<VersionProfile> BuiltInProfiles()
        {
            yield return new VersionProfile
            {
                Id = "1.24e",
                DisplayName = "1.24e",
                VersionPrefixes = new[] { "1.24." },
                CguiGlobalRva = 0xACBDD8,
                ChatBarOffset = 0x3FC,
                TextObjectOffset = 0x1E0,
                TextBufferOffset = 0x1E4,
                IsChattingRva = 0xAE8450,
                EscapeMenuOffset = 0x228,
                UnitListRva = 0xACD44C,
                CommandBarOffset = 0x3C8,
                CommandGridOffset = 0x154,
                CommandFrameButtonOffset = 0x190,
                CommandButtonHotkeyOffset = 0x5AC,
                CameraSetFieldRva = 0x3B53F0,
                StatusBarSupported = true,
                StatusBarAllocCallRva = 0x37A623,
                StatusBarUpdateCallRva = 0x37AA28,
                StatusBarOriginalUpdateRva = 0x2C7FD0,
                StatusBarCtorRva = 0x35A800,
                StatusBarGetUnitStateRva = 0x27B9B0,
                StatusBarUnitScaleRva = 0x2698A0,
                StatusBarSetBarPositionRva = 0x606950,
                StatusBarSetBarBoundsRva = 0x606460,
                StatusBarGetUnitUIDefRva = 0,
                StatusBarIsUnitStructureRva = 0,
                StatusBarGetLocalPlayerRva = 0x3BC6A0,
                StatusBarGetCPlayerWar3Rva = 0x3BE010,
                StatusBarCPlayerAttrRva = 0x41BF60,
                StatusBarIntegerGetValueRva = 0x473C80,
                StatusBarHealthFlagOffset = 0x1D8,
                NativeChat = true,
                SkillConvert = true,
                CameraDistance = true
            };
            yield return new VersionProfile
            {
                Id = "1.20e",
                DisplayName = "1.20e",
                VersionPrefixes = new[] { "1.20." },
                CguiGlobalRva = 0x84BC70,
                ChatBarOffset = 0x3F0,
                TextObjectOffset = 0x1E0,
                TextBufferOffset = 0x1E4,
                IsChattingRva = 0x84BBF8,
                EscapeMenuOffset = 0x21C,
                CommandBarOffset = 0x3BC,
                CommandGridOffset = 0x154,
                CommandFrameButtonOffset = 0x190,
                CommandButtonHotkeyOffset = 0x5AC,
                CameraSetFieldRva = 0x2DD610,
                StatusBarSupported = true,
                StatusBarAllocCallRva = 0x166A14,
                StatusBarUpdateCallRva = 0x167639,
                StatusBarOriginalUpdateRva = 0x1D63D0,
                StatusBarCtorRva = 0x1529A0,
                StatusBarGetUnitStateRva = 0x1C0030,
                StatusBarUnitScaleRva = 0,
                StatusBarSetBarPositionRva = 0x13780,
                StatusBarSetBarBoundsRva = 0x13AB0,
                StatusBarGetUnitUIDefRva = 0,
                StatusBarIsUnitStructureRva = 0,
                StatusBarGetLocalPlayerRva = 0x8722BC,
                StatusBarGetCPlayerWar3Rva = 0,
                StatusBarCPlayerAttrRva = 0x314580,
                StatusBarIntegerGetValueRva = 0x088E10,
                StatusBarHealthFlagOffset = 0,
                NativeChat = true,
                SkillConvert = true,
                CameraDistance = true
            };
            yield return new VersionProfile
            {
                Id = "1.27a",
                DisplayName = "1.27a",
                VersionPrefixes = new[] { "1.27." },
                CguiGlobalRva = 0xBE6350,
                ChatBarOffset = 0x3FC,
                TextObjectOffset = 0x1E0,
                TextBufferOffset = 0x1E4,
                IsChattingRva = 0xBDAA14,
                EscapeMenuOffset = 0x228,
                SelectionOffset = 0x3F4,
                ActiveUnitOffset = 0x238,
                CommandBarOffset = 0x3C8,
                CommandGridOffset = 0x154,
                CommandFrameButtonOffset = 0x190,
                CommandButtonHotkeyOffset = 0x5AC,
                CameraSetFieldRva = 0x1F4170,
                StatusBarSupported = true,
                StatusBarAllocCallRva = 0x374F14,
                StatusBarUpdateCallRva = 0x3784CA,
                StatusBarOriginalUpdateRva = 0x6374A0,
                StatusBarCtorRva = 0x383F60,
                StatusBarGetUnitStateRva = 0x669B40,
                StatusBarUnitScaleRva = 0,
                StatusBarSetBarPositionRva = 0x0BD830,
                StatusBarSetBarBoundsRva = 0x0BD630,
                StatusBarGetUnitUIDefRva = 0x327020,
                StatusBarIsUnitStructureRva = 0x66EA60,
                StatusBarGetLocalPlayerRva = 0x1E3150,
                StatusBarGetCPlayerWar3Rva = 0x1D03D0,
                StatusBarCPlayerAttrRva = 0x23DE10,
                StatusBarIntegerGetValueRva = 0x039860,
                StatusBarHealthFlagOffset = 0x1D8,
                NativeChat = true,
                SkillConvert = true,
                CameraDistance = true
            };
        }

        private sealed class ProfileDto
        {
            public string Id { get; set; }
            public string DisplayName { get; set; }
            public string[] VersionPrefixes { get; set; }
            public ChatDto Chat { get; set; }
            public UiDto Ui { get; set; }
            public SkillsDto Skills { get; set; }
            public CameraDto Camera { get; set; }
            public StatusBarsDto StatusBars { get; set; }
            public CapabilitiesDto Capabilities { get; set; }
        }

        private sealed class SkillsDto
        {
            public string UnitListRva { get; set; }
            public string SelectionOffset { get; set; }
            public string ActiveUnitOffset { get; set; }
            public string CommandBarOffset { get; set; }
            public string CommandGridOffset { get; set; }
            public string CommandFrameButtonOffset { get; set; }
            public string CommandButtonHotkeyOffset { get; set; }
        }

        private sealed class ChatDto
        {
            public string CguiGlobalRva { get; set; }
            public string ChatBarOffset { get; set; }
            public string TextObjectOffset { get; set; }
            public string TextBufferOffset { get; set; }
            public string IsChattingRva { get; set; }
        }

        private sealed class UiDto
        {
            public string EscapeMenuOffset { get; set; }
        }

        private sealed class CameraDto
        {
            public string SetFieldRva { get; set; }
        }

        private sealed class StatusBarsDto
        {
            public bool Supported { get; set; }
            public string AllocCallRva { get; set; }
            public string UpdateCallRva { get; set; }
            public string OriginalUpdateRva { get; set; }
            public string CtorRva { get; set; }
            public string GetUnitStateRva { get; set; }
            public string UnitScaleRva { get; set; }
            public string SetBarPositionRva { get; set; }
            public string SetBarBoundsRva { get; set; }
            public string GetUnitUIDefRva { get; set; }
            public string IsUnitStructureRva { get; set; }
            public string GetLocalPlayerRva { get; set; }
            public string GetCPlayerWar3Rva { get; set; }
            public string CPlayerAttrRva { get; set; }
            public string IntegerGetValueRva { get; set; }
            public string HealthFlagOffset { get; set; }
        }

        private sealed class CapabilitiesDto
        {
            public bool NativeChat { get; set; }
            public bool SkillConvert { get; set; }
            public bool CameraDistance { get; set; }
        }
    }
}
