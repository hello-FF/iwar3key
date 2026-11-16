using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using IWar3Key.Shell.Core;

namespace IWar3Key.Shell.Interop
{
    public enum SessionState
    {
        Idle,
        Detected,
        Attaching,
        Attached,
        Error
    }

    public sealed class SessionSnapshot
    {
        public SessionState State;
        public int Pid;
        public IntPtr Window;
        public string VersionText;
        public string VersionLabel;
        public string Message;
        public bool Attached;
        public bool Paused;
        public bool NeedsRestart;
    }

    public sealed class GameSession : IDisposable
    {
        private readonly AppPaths _paths;
        private readonly AppConfig _config;
        private readonly ConfigStore _store;
        private readonly ProfileCatalog _profiles;
        private readonly Logger _log;
        private readonly Dispatcher _dispatcher;
        private readonly DispatcherTimer _timer;
        private readonly VideoRegistry _video;
        private readonly int? _preferredPid;
        private readonly WindowStyler _windowStyler = new WindowStyler();
        private readonly object _consumedLock = new object();
        private readonly object _pauseLock = new object();
        private readonly object _skillApplyLock = new object();
        private readonly HashSet<int> _consumedKeys = new HashSet<int>();
        private readonly Dictionary<string, long> _macroCooldowns = new Dictionary<string, long>();

        private GameProcessInfo _game;
        private AgentClient _agent;
        private volatile AgentClient _agentRef;
        private VersionProfile _profile;
        private SessionState _state = SessionState.Idle;
        private string _message = "未检测到游戏";
        private bool _attaching;
        private bool _needsRestart;
        private bool _polling;
        private bool _disposed;
        private volatile bool _paused;
        private volatile bool _chatOpen;
        private volatile bool _menuOpen;
        private bool _windowApplyPending;
        private DateTime _windowApplyNotBefore = DateTime.MinValue;
        private const int WindowApplyDelayMs = 500;
        private GameUiStateReader _uiStateReader;
        private volatile bool _chatAvailable;
        private volatile bool _chatReady;
        private volatile bool _skillProfileApplied;
        private volatile bool _cameraProfileApplied;
        private volatile bool _statusBarProfileApplied;
        private volatile bool _skillConvertActive;
        private volatile int _gamePid;
        private Task _skillApplyTail = Task.CompletedTask;
        private DateTime _lastAttachAttempt = DateTime.MinValue;
        private int _lastAttachPid;
        private volatile RuleSet _rules;
        private KeyboardHook _hook;
        private MouseClipService _mouseClip;

        public GameSession(AppPaths paths, ConfigStore store, ProfileCatalog profiles, Logger log, int? preferredPid = null)
        {
            _paths = paths;
            _store = store;
            _profiles = profiles;
            _log = log;
            _preferredPid = preferredPid;
            _config = store.Config;
            _dispatcher = Dispatcher.CurrentDispatcher;
            _video = new VideoRegistry(paths, log);
            _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(900) };
            _timer.Tick += OnTimerTick;
            // Diagnostics only: key conversion itself happens inside the Agent
            // window procedure and does not depend on this UI-side poll.
        }

        public event Action<SessionSnapshot> SnapshotChanged;

        public void Start()
        {
            _video.RestoreIfPending(true);
            RebuildRules();
            _store.Saved += OnConfigSaved;

            _hook = new KeyboardHook(OnHookKey, ex => _log.Error("键盘钩子异常", ex));
            if (!_hook.Start())
            {
                _log.Warn("键盘钩子启动失败，改键与喊话热键不可用。");
            }

            _mouseClip = new MouseClipService(
                () => _game == null ? IntPtr.Zero : _game.Window,
                () => _config.Window.Enabled && _config.Window.ClipMouse,
                _log);

            _timer.Start();
            OnTimerTick(null, EventArgs.Empty);
            _log.Info("会话已启动。");
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _timer.Stop();
            _store.Saved -= OnConfigSaved;

            try { _hook?.Dispose(); } catch { }
            try { _mouseClip?.Dispose(); } catch { }

            if (_agent != null)
            {
                try { _agent.Dispose(); } catch { }
                _agent = null;
                _agentRef = null;
            }

            CloseUiStateReader();

            if (_game != null && _game.Window != IntPtr.Zero)
            {
                try { _windowStyler.Restore(_game.Window); } catch { }
            }

            NativeMethods.ClipCursor(IntPtr.Zero);
            _video.RestoreIfPending(true);
            _log.Info("会话已关闭。");
        }

        public void TogglePause()
        {
            TogglePauseInternal(false);
        }

        public bool LaunchGame()
        {
            string path = _config.Game.Path;
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                SetMessage("游戏路径无效，请先在设置中选择 war3.exe。");
                _log.Warn("启动游戏失败：路径无效 " + path);
                return false;
            }

            if (!Path.GetFileName(path).Equals("war3.exe", StringComparison.OrdinalIgnoreCase))
            {
                SetMessage("选择的不是 war3.exe。");
                return false;
            }

            if (_game != null)
            {
                SetMessage("游戏已在运行。");
                return false;
            }

            try
            {
                if (_config.Window.Enabled)
                {
                    _video.Apply(_config.Window.Width, _config.Window.Height);
                }

                var startInfo = new ProcessStartInfo
                {
                    FileName = path,
                    Arguments = "-window",
                    WorkingDirectory = Path.GetDirectoryName(path) ?? _paths.BaseDirectory,
                    UseShellExecute = false
                };
                Process.Start(startInfo);
                _message = "正在启动游戏…";
                _log.Info("已启动游戏: " + path);
                RaiseSnapshot();
                return true;
            }
            catch (Exception ex)
            {
                _log.Error("启动游戏失败", ex);
                SetMessage("启动游戏失败: " + ex.Message);
                return false;
            }
        }

        public async Task AttachNowAsync()
        {
            if (_disposed || _attaching)
            {
                return;
            }

            GameProcessInfo game = _game;
            if (game == null)
            {
                SetMessage("未检测到游戏。");
                return;
            }

            _lastAttachAttempt = DateTime.UtcNow;
            _lastAttachPid = game.Pid;

            if (!PathMatchesGame())
            {
                SetError("检测到的游戏与设置中的路径不一致。");
                return;
            }

            if (!game.HasWindow || !NativeMethods.IsWindow(game.Window))
            {
                game.Window = GameProcessFinder.FindWindow(game.Pid);
                if (!game.HasWindow)
                {
                    SetMessage("等待游戏窗口出现…");
                    return;
                }
            }

            if (_state == SessionState.Attached && _agent != null && _agent.Connected)
            {
                RebuildRules();
                ApplyWindow();
                await SendConfigAsync().ConfigureAwait(true);
                SetMessage("已重新应用设置。");
                return;
            }

            _attaching = true;
            _state = SessionState.Attaching;
            _message = "正在校验并注入 Agent…";
            RaiseSnapshot();
            _log.Info("开始附加 pid=" + game.Pid + " path=" + game.ExePath);

            AgentClient client = null;
            try
            {
                InjectionResult injection = await Task.Run(() => Injector.Inject(game.Pid, _paths.AgentDllPath)).ConfigureAwait(true);
                if (!injection.Success)
                {
                    SetError(injection.Message);
                    return;
                }

                _log.Info(injection.Message);

                client = new AgentClient(game.Pid);
                bool connected = await Task.Run(() => client.Connect(10000)).ConfigureAwait(true);
                if (!connected)
                {
                    client.Dispose();
                    client = null;
                    SetError(injection.AlreadyLoaded
                        ? "Agent 已加载但未响应。请重启游戏后再试。"
                        : "Agent 已注入但未能建立连接。请重启游戏后再试。");
                    return;
                }

                AgentResponse hello = await client.HelloAsync().ConfigureAwait(true);
                AgentInfo info = hello.Ok ? AgentInfo.Parse(hello) : null;
                if (info == null)
                {
                    client.Dispose();
                    client = null;
                    SetError("Agent 握手失败: " + hello.Message);
                    return;
                }

                if (info.Abi != AgentClient.ProtocolAbi)
                {
                    client.Dispose();
                    client = null;
                    SetError("Agent ABI 不匹配，请重新构建工具。");
                    return;
                }

                string version = string.IsNullOrWhiteSpace(info.GameVersion) ? game.VersionText : info.GameVersion;
                game.VersionText = version;
                VersionProfile profile = _profiles.Match(version);
                bool chatAvailable = false;

                if (profile != null && profile.NativeChat && profile.CguiGlobalRva > 0)
                {
                    AgentResponse profileResponse = await client.SendProfileAsync(profile).ConfigureAwait(true);
                    chatAvailable = profileResponse.Ok;
                    if (!profileResponse.Ok)
                    {
                        _log.Warn("应用版本档案失败: " + profileResponse.Message);
                    }
                    else
                    {
                        _log.Info("已应用版本档案: " + profile.Id);
                    }
                }

                await client.ConfigureAsync(_config.Chat.SendDelayMs).ConfigureAwait(true);

                bool skillApplied = false;
                _skillProfileApplied = false;
                _cameraProfileApplied = false;
                _statusBarProfileApplied = false;
                StopSkillConversion();
                _profile = profile;
                _uiStateReader?.Dispose();
                _uiStateReader = GameUiStateReader.TryOpen(
                    game.Pid,
                    info.GameBase,
                    profile == null ? 0 : profile.IsChattingRva,
                    profile == null ? 0 : profile.CguiGlobalRva,
                    profile == null ? 0 : profile.EscapeMenuOffset);
                if (_uiStateReader != null)
                {
                    if (_uiStateReader.ChatAvailable)
                    {
                        _log.Info(string.Format(
                            "聊天状态: 原生检测 0x{0:X}",
                            _uiStateReader.ChatAddress));
                    }
                    else
                    {
                        _log.Warn("聊天状态: 无原生检测，聊天时改键不受保护");
                    }

                    if (_uiStateReader.MenuAvailable)
                    {
                        _log.Info(string.Format(
                            "菜单状态: 原生检测 CGameUI 0x{0:X} + 0x{1:X}",
                            _uiStateReader.CguiGlobalAddress,
                            _uiStateReader.EscapeMenuOffset));
                        if (_uiStateReader.TryReadMenu(out bool menuNow))
                        {
                            _log.Info("菜单状态: 当前" + (menuNow ? "打开" : "关闭"));
                        }
                    }
                }
                else
                {
                    _log.Warn("聊天状态: 无原生检测，聊天时改键不受保护");
                    _log.Warn("菜单状态: 无原生检测，菜单快捷键可能被改键拦截");
                }
                // Command-card conversion needs the CGameUI -> command bar ->
                // grid -> button chain. The selected-unit chain is optional
                // (learn-page recognition only), so 1.20e can convert skills
                // with command-card offsets alone.
                bool hasCommandCard = profile != null &&
                    profile.CguiGlobalRva > 0 &&
                    profile.CommandBarOffset > 0 &&
                    profile.CommandGridOffset > 0 &&
                    profile.CommandFrameButtonOffset > 0 &&
                    profile.CommandButtonHotkeyOffset > 0;
                bool skillCapable = hasCommandCard && profile.SkillConvert;
                if (skillCapable)
                {
                    AgentResponse skillProfileResponse = await client.SendSkillProfileAsync(
                        profile.UnitListRva,
                        profile.CguiGlobalRva,
                        profile.SelectionOffset,
                        profile.ActiveUnitOffset,
                        profile.CommandBarOffset,
                        profile.CommandGridOffset,
                        profile.CommandFrameButtonOffset,
                        profile.CommandButtonHotkeyOffset).ConfigureAwait(true);
                    if (skillProfileResponse.Ok)
                    {
                        _skillProfileApplied = true;
                        skillApplied = true;
                        _log.Info(string.Format(
                            "已应用技能档案: {0} unitListRva=0x{1:X} selectionOffset=0x{2:X} activeUnitOffset=0x{3:X}",
                            profile.DisplayName,
                            profile.UnitListRva,
                            profile.SelectionOffset,
                            profile.ActiveUnitOffset));
                        await QueueSkillApply(client, null).ConfigureAwait(true);
                    }
                    else
                    {
                        _log.Warn("应用技能档案失败: " + skillProfileResponse.Message);
                    }
                }

                bool cameraApplied = false;
                _cameraProfileApplied = false;
                if (profile != null && profile.CameraDistance &&
                    profile.CguiGlobalRva > 0 && profile.CameraSetFieldRva > 0)
                {
                    AgentResponse cameraProfileResponse = await client.SendCameraProfileAsync(
                        profile.CameraSetFieldRva,
                        profile.CguiGlobalRva,
                        profile.ChatBarOffset,
                        profile.TextObjectOffset,
                        profile.TextBufferOffset).ConfigureAwait(true);
                    if (cameraProfileResponse.Ok)
                    {
                        _cameraProfileApplied = true;
                        cameraApplied = true;
                        _log.Info(string.Format(
                            "已应用视距档案: {0} setFieldRva=0x{1:X}",
                            profile.DisplayName,
                            profile.CameraSetFieldRva));
                    }
                    else
                    {
                        _log.Warn("应用视距档案失败: " + cameraProfileResponse.Message);
                    }
                }

                _statusBarProfileApplied = false;
                if (profile != null && profile.StatusBarSupported &&
                    profile.CguiGlobalRva > 0 &&
                    profile.StatusBarAllocCallRva > 0 &&
                    profile.StatusBarUpdateCallRva > 0 &&
                    profile.StatusBarOriginalUpdateRva > 0 &&
                    profile.StatusBarCtorRva > 0 &&
                    profile.StatusBarGetUnitStateRva > 0 &&
                    profile.StatusBarSetBarPositionRva > 0 &&
                    profile.StatusBarSetBarBoundsRva > 0 &&
                    profile.StatusBarGetLocalPlayerRva > 0 &&
                    profile.StatusBarCPlayerAttrRva > 0 &&
                    profile.StatusBarIntegerGetValueRva > 0)
                {
                    AgentResponse statusBarProfileResponse = await client.SendStatusBarProfileAsync(
                        profile.StatusBarAllocCallRva,
                        profile.StatusBarUpdateCallRva,
                        profile.StatusBarOriginalUpdateRva,
                        profile.StatusBarCtorRva,
                        profile.StatusBarGetUnitStateRva,
                        profile.StatusBarUnitScaleRva,
                        profile.StatusBarSetBarPositionRva,
                        profile.StatusBarSetBarBoundsRva,
                        profile.StatusBarGetUnitUIDefRva,
                        profile.StatusBarIsUnitStructureRva,
                        profile.StatusBarGetLocalPlayerRva,
                        profile.StatusBarGetCPlayerWar3Rva,
                        profile.StatusBarCPlayerAttrRva,
                        profile.StatusBarIntegerGetValueRva,
                        profile.CguiGlobalRva,
                        profile.StatusBarHealthFlagOffset).ConfigureAwait(true);
                    if (statusBarProfileResponse.Ok)
                    {
                        _statusBarProfileApplied = true;
                        _log.Info(string.Format(
                            "已应用状态条档案: {0} allocCallRva=0x{1:X} updateCallRva=0x{2:X}",
                            profile.DisplayName,
                            profile.StatusBarAllocCallRva,
                            profile.StatusBarUpdateCallRva));
                    }
                    else
                    {
                        _log.Warn("应用状态条档案失败: " + statusBarProfileResponse.Message);
                    }
                }

                client.Disconnected += () => OnAgentDisconnected(client);
                _agent = client;
                _agentRef = client;
                _chatAvailable = chatAvailable;
                _chatReady = false;
                _chatOpen = false;
                _menuOpen = false;
                _gamePid = game.Pid;

                if (_config.Window.Enabled &&
                    (_config.Window.Borderless || _config.Window.AlwaysOnTop))
                {
                    _windowStyler.ApplyStyle(game.Window, _config.Window, _log);
                }

                ScheduleWindowApply();
                await client.SetPausedAsync(_paused).ConfigureAwait(true);
                await SendCameraAsync().ConfigureAwait(true);
                await SendStatusBarAsync().ConfigureAwait(true);

                _state = SessionState.Attached;
                string label = profile == null ? LabelFor(version) : profile.DisplayName;
                _message = "已附加 · " + label +
                           (chatAvailable ? " · 快捷喊话可用" : " · 安全模式") +
                           (skillApplied ? " · 技能改键" : "") +
                           (cameraApplied ? " · 视距" : "");
                _log.Info(string.Format(
                    "附加成功 pid={0} version={1} base=0x{2:X} chat={3} skills={4}",
                    game.Pid,
                    version,
                    info.GameBase,
                    chatAvailable,
                    skillApplied));
            }
            catch (Exception ex)
            {
                if (client != null)
                {
                    client.Dispose();
                    client = null;
                }

                _agent = null;
                _agentRef = null;
                CloseUiStateReader();
                _chatAvailable = false;
                _skillProfileApplied = false;
                _cameraProfileApplied = false;
                _statusBarProfileApplied = false;
                StopSkillConversion();
                SetError("附加异常: " + ex.Message);
                _log.Error("附加异常", ex);
            }
            finally
            {
                _attaching = false;
                RaiseSnapshot();
            }
        }

        private void OnTimerTick(object sender, EventArgs e)
        {
            if (_disposed)
            {
                return;
            }

            try
            {
                if (_game != null)
                {
                    PollExistingGame();
                }
                else
                {
                    DetectGame();
                }

                if (_windowApplyPending && DateTime.UtcNow >= _windowApplyNotBefore)
                {
                    _windowApplyPending = false;
                    if (_game != null)
                    {
                        ApplyWindow();
                    }
                }
            }
            catch (Exception ex)
            {
                _log.Error("会话轮询异常", ex);
            }
        }

        private void DetectGame()
        {
            List<GameProcessInfo> candidates = GameProcessFinder.FindCandidates(_config.Game.Path);
            if (_preferredPid.HasValue)
            {
                candidates = candidates.Where(p => p.Pid == _preferredPid.Value).ToList();
            }

            if (candidates.Count == 0)
            {
                if (_state != SessionState.Idle)
                {
                    _state = SessionState.Idle;
                    _message = _preferredPid.HasValue
                        ? "未找到指定 PID 的游戏 (" + _preferredPid.Value + ")"
                        : "未检测到游戏";
                    RaiseSnapshot();
                }

                return;
            }

            _game = candidates[0];
            _gamePid = _game.Pid;
            _state = SessionState.Detected;
            _message = "检测到 " + LabelFor(_game.VersionText) + (_game.HasWindow ? string.Empty : "（等待窗口）");
            _log.Info("检测到游戏 pid=" + _game.Pid + " version=" + _game.VersionText + " path=" + _game.ExePath);
            RaiseSnapshot();

            if (_config.Game.AutoApply && CanAutoAttach())
            {
                _ = AttachNowAsync();
            }
        }

        private void PollExistingGame()
        {
            if (!IsProcessAlive(_game.Pid))
            {
                HandleGameExited();
                return;
            }

            bool windowChanged = false;
            if (_game.Window == IntPtr.Zero || !NativeMethods.IsWindow(_game.Window))
            {
                IntPtr window = GameProcessFinder.FindWindow(_game.Pid);
                if (window != IntPtr.Zero)
                {
                    _game.Window = window;
                    windowChanged = true;
                }
            }

            if (windowChanged && _state == SessionState.Attached)
            {
                // A pending startup apply is deliberately delayed until the
                // main menu has finished laying out; don't apply early just
                // because the window handle changed.
                if (!_windowApplyPending)
                {
                    ApplyWindow();
                }

                RaiseSnapshot();
            }

            if (_state == SessionState.Detected && _config.Game.AutoApply && CanAutoAttach())
            {
                _ = AttachNowAsync();
            }

            if (_state == SessionState.Attached && _agent != null && _agent.Connected)
            {
                PollAgentState();
            }
        }

        private async void PollAgentState()
        {
            if (_polling)
            {
                return;
            }

            AgentClient agent = _agent;
            if (agent == null || !agent.Connected)
            {
                return;
            }

            _polling = true;
            try
            {
                AgentResponse response = await agent.GetStateAsync(1500).ConfigureAwait(true);
                if (response.Ok && response.Fields.Length >= 4)
                {
                    bool chatReady = response.Fields[3] == "1";
                    if (chatReady != _chatReady)
                    {
                        _chatReady = chatReady;
                        RaiseSnapshot();
                    }
                }
            }
            catch
            {
                // A transient timeout is not a disconnect; the reader thread owns that state.
            }
            finally
            {
                _polling = false;
            }
        }

        private void OnAgentDisconnected(AgentClient client)
        {
            _dispatcher.BeginInvoke(new Action(() =>
            {
                if (_disposed || !ReferenceEquals(_agent, client))
                {
                    return;
                }

                _agent = null;
                _agentRef = null;
                CloseUiStateReader();
                _chatAvailable = false;
                _chatReady = false;
                _chatOpen = false;
                _menuOpen = false;
                _skillProfileApplied = false;
                _cameraProfileApplied = false;
                _statusBarProfileApplied = false;
                StopSkillConversion();
                try { client.Dispose(); } catch { }
                if (_state == SessionState.Attached)
                {
                    _state = SessionState.Detected;
                    _message = "Agent 连接断开，可重新应用。";
                }

                _log.Warn("Agent 连接断开。");
                RaiseSnapshot();
            }));
        }

        private void HandleGameExited()
        {
            IntPtr window = _game == null ? IntPtr.Zero : _game.Window;
            _log.Info("游戏已退出。");

            AgentClient agent = _agent;
            _agent = null;
            _agentRef = null;
            if (agent != null)
            {
                try { agent.Dispose(); } catch { }
            }

            if (window != IntPtr.Zero)
            {
                _windowStyler.Forget(window);
            }

            _game = null;
            _gamePid = 0;
            _profile = null;
            _windowApplyPending = false;
            CloseUiStateReader();
            _chatAvailable = false;
            _chatReady = false;
            _chatOpen = false;
            _menuOpen = false;
            _skillProfileApplied = false;
            _cameraProfileApplied = false;
            _statusBarProfileApplied = false;
            StopSkillConversion();
            _needsRestart = false;
            _state = SessionState.Idle;
            _message = "游戏已退出";
            _video.RestoreIfPending(false);
            RaiseSnapshot();
        }

        private bool CanAutoAttach()
        {
            if (_attaching || _game == null || !_game.HasWindow)
            {
                return false;
            }

            if (_state != SessionState.Detected || !PathMatchesGame())
            {
                return false;
            }

            return _lastAttachPid != _game.Pid ||
                   (DateTime.UtcNow - _lastAttachAttempt).TotalSeconds >= 15;
        }

        private bool PathMatchesGame()
        {
            string preferred = _config.Game.Path;
            if (string.IsNullOrWhiteSpace(preferred) || !File.Exists(preferred))
            {
                return true;
            }

            return _game != null && _game.PathMatchesPreference;
        }

        private void ScheduleWindowApply()
        {
            _windowApplyPending = true;
            _windowApplyNotBefore = DateTime.UtcNow.AddMilliseconds(WindowApplyDelayMs);
            _log.Info("窗口设置延迟应用：等待主菜单完成布局。");
        }

        private void ApplyWindow()
        {
            _windowApplyPending = false;
            if (_game == null || _game.Window == IntPtr.Zero)
            {
                return;
            }

            if (_config.Window.Enabled)
            {
                // 1.24e can crash if its window is resized/styled while the
                // client area is still 0x0 during startup. Wait until the
                // window is actually visible and has a valid client area.
                if (!NativeMethods.IsWindowVisible(_game.Window) ||
                    !NativeMethods.GetClientRect(_game.Window, out NativeMethods.RECT ready) ||
                    ready.Width <= 0 || ready.Height <= 0)
                {
                    _log.Warn("游戏窗口尚未就绪，跳过本次窗口设置。");
                    return;
                }

                _windowStyler.Apply(_game.Window, _config.Window, _log);
                // Width/Height are outer-frame dimensions. The client area is
                // necessarily smaller, so a client mismatch is not a restart
                // condition; WindowStyler logs the actual frame/client values.
                _needsRestart = false;
            }
            else
            {
                _windowStyler.Restore(_game.Window);
                _needsRestart = false;
            }
        }

        private async Task SendConfigAsync()
        {
            AgentClient agent = _agent;
            if (agent == null || !agent.Connected)
            {
                return;
            }

            try
            {
                AgentResponse response = await agent.ConfigureAsync(_config.Chat.SendDelayMs).ConfigureAwait(true);
                if (!response.Ok)
                {
                    _log.Warn("下发 Agent 配置失败: " + response.Message);
                }

                await SendCameraAsync().ConfigureAwait(true);
                await SendStatusBarAsync().ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                _log.Error("下发 Agent 配置异常", ex);
            }
        }

        private async Task SendCameraAsync()
        {
            AgentClient agent = _agent;
            if (agent == null || !agent.Connected || !_cameraProfileApplied)
            {
                return;
            }

            bool enabled = _config.Camera.Enabled;
            try
            {
                AgentResponse response = await agent.SendCameraAsync(
                    enabled,
                    _config.Camera.MaxDistance,
                    _config.Camera.ZoomOutHotkey,
                    _config.Camera.ZoomInHotkey).ConfigureAwait(true);
                if (!response.Ok)
                {
                    _log.Warn("下发视距设置失败: " + response.Message);
                }
            }
            catch (Exception ex)
            {
                _log.Error("下发视距设置异常", ex);
            }
        }

        private async Task SendStatusBarAsync()
        {
            AgentClient agent = _agent;
            if (agent == null || !agent.Connected || !_statusBarProfileApplied)
            {
                return;
            }

            try
            {
                bool health = _config.StatusBar.HealthEnabled;
                AgentResponse response = await agent.SendStatusBarAsync(
                    health,
                    health && _config.StatusBar.FriendlyMana,
                    health && _config.StatusBar.EnemyMana).ConfigureAwait(true);
                if (!response.Ok)
                {
                    _log.Warn("下发状态条设置失败: " + response.Message);
                }
            }
            catch (Exception ex)
            {
                _log.Error("下发状态条设置异常", ex);
            }
        }

        // Pause/unpause and config saves are fire-and-forget from the keyboard
        // hook. Serialize them so a late pause command can never land after an
        // unpause and leave skill conversion disabled.
        private Task QueueSkillApply(AgentClient client, bool? forceEnabled)
        {
            lock (_skillApplyLock)
            {
                Task task = _skillApplyTail.ContinueWith(
                    _ => ApplySkillsAsync(client, forceEnabled),
                    CancellationToken.None,
                    TaskContinuationOptions.None,
                    TaskScheduler.Default).Unwrap();
                _skillApplyTail = task;
                return task;
            }
        }

        private async Task ApplySkillsAsync(AgentClient client, bool? forceEnabled = null)
        {
            if (client == null || !client.Connected)
            {
                return;
            }

            bool convertSupported = _profile != null &&
                                    _profile.SkillConvert &&
                                    _profile.CguiGlobalRva > 0 &&
                                    _profile.CommandBarOffset > 0 &&
                                    _profile.CommandGridOffset > 0 &&
                                    _profile.CommandFrameButtonOffset > 0 &&
                                    _profile.CommandButtonHotkeyOffset > 0;
            bool convertActive = convertSupported &&
                                 _config.Input.SkillsEnabled &&
                                 !_paused &&
                                 (forceEnabled ?? true);
            var uiKeys = new string[12];
            var uiQuickCast = new string[12];
            int skipped = 0;

            for (int i = 0; i < 12; i++)
            {
                SkillSlot slot = i < _config.Input.Skills.Count ? _config.Input.Skills[i] : null;
                string key = slot?.Key?.Trim();
                if (string.IsNullOrEmpty(key))
                {
                    uiKeys[i] = "-";
                    uiQuickCast[i] = "0";
                    continue;
                }

                bool supported = key.Length == 1 &&
                                 ((key[0] >= 'A' && key[0] <= 'Z') ||
                                  (key[0] >= 'a' && key[0] <= 'z') ||
                                  (key[0] >= '0' && key[0] <= '9'));
                if (supported)
                {
                    uiKeys[i] = char.ToUpperInvariant(key[0]).ToString();
                    uiQuickCast[i] = slot.QuickCast ? "1" : "0";
                }
                else
                {
                    uiKeys[i] = "-";
                    uiQuickCast[i] = "0";
                    skipped++;
                }
            }

            // The Agent's SKILLS protocol is bottom-row-first. The UI mirrors the
            // game command card top-to-bottom, so flip the three rows here:
            // UI row 0 (top) -> agent row 2, UI row 2 (bottom) -> agent row 0.
            var keys = new string[12];
            var quickCast = new string[12];
            for (int ui = 0; ui < 12; ui++)
            {
                int row = ui / 4;
                int column = ui % 4;
                int agentIndex = (2 - row) * 4 + column;
                keys[agentIndex] = uiKeys[ui];
                quickCast[agentIndex] = uiQuickCast[ui];
            }

            var itemQuickCast = new string[6];
            for (int i = 0; i < 6; i++)
            {
                ItemSlot item = i < _config.Input.Items.Count ? _config.Input.Items[i] : null;
                itemQuickCast[i] = _config.Input.Enabled && _config.Input.ItemsEnabled &&
                                   item != null && item.QuickCast &&
                                   !string.IsNullOrWhiteSpace(item.Key)
                    ? "1"
                    : "0";
            }

            try
            {
                // Conversion never writes ability data. SKILLS stores the
                // configured keys and quick-cast flags; SKILLINFO enables or
                // disables the Agent's per-key command-card read.
                AgentResponse response = await client.SendSkillsAsync(keys, quickCast, itemQuickCast).ConfigureAwait(true);
                if (!response.Ok)
                {
                    _log.Warn("下发技能绑定失败: " + response.Message);
                    return;
                }

                _skillConvertActive = convertActive;
                await client.SendSkillInfoAsync(convertActive).ConfigureAwait(true);

                string valid = response.Fields.Length > 2 ? response.Fields[2] : "?";
                if (convertActive)
                {
                    _log.Info("技能按键转换: 已启用 (每次按键读取当前命令栏 12 格热键)");
                }
                else
                {
                    _log.Info("技能按键转换: 未启用 (当前版本没有可用的技能链或已暂停)");
                }

                if (skipped > 0 && convertActive)
                {
                    _log.Warn("技能改键仅支持单字符键，已跳过 " + skipped + " 个组合键。");
                }
            }
            catch (Exception ex)
            {
                _log.Error("下发技能绑定异常", ex);
            }
        }

        private void StopSkillConversion()
        {
            _skillConvertActive = false;
        }

        private void OnConfigSaved(string time)
        {
            RebuildRules();

            if (_game != null && _state == SessionState.Error && PathMatchesGame())
            {
                _state = SessionState.Detected;
                _message = "设置已更新，可重新应用。";
            }

            if (_state == SessionState.Attached)
            {
                ApplyWindow();
                _ = SendConfigAsync();
                if (_skillProfileApplied && _agent != null && _agent.Connected)
                {
                    _ = QueueSkillApply(_agent, null);
                }
            }

            RaiseSnapshot();
        }

        private void RebuildRules()
        {
            var rules = new RuleSet
            {
                ItemsEnabled = _config.Input.Enabled && _config.Input.ItemsEnabled,
                MappingEnabled = _config.Input.MappingEnabled,
                Panic = ParsedHotkey.Parse(_config.Game.PanicHotkey),
                Apply = ParsedHotkey.Parse(_config.Game.ApplyHotkey)
            };

            var macros = new List<MacroRule>();
            foreach (ChatMacro macro in _config.Chat.Macros)
            {
                if (!macro.Enabled || string.IsNullOrWhiteSpace(macro.Text))
                {
                    continue;
                }

                ParsedHotkey hotkey = ParsedHotkey.Parse(macro.Binding);
                if (!hotkey.Valid)
                {
                    continue;
                }

                macros.Add(new MacroRule
                {
                    Id = string.IsNullOrWhiteSpace(macro.Id) ? macro.Binding : macro.Id,
                    Hotkey = hotkey,
                    Text = macro.Text,
                    Channel = string.Equals(macro.Channel, "ally", StringComparison.OrdinalIgnoreCase) ? 1 : 0,
                    CooldownMs = _config.Chat.AntiSpam ? Math.Max(0, _config.Chat.CooldownMs) : 0
                });
            }

            rules.Macros = macros.ToArray();

            var items = new List<ItemRule>();
            foreach (ItemSlot item in _config.Input.Items)
            {
                if (string.IsNullOrWhiteSpace(item.Key))
                {
                    continue;
                }

                ParsedHotkey hotkey = ParsedHotkey.Parse(item.Key);
                if (!hotkey.Valid)
                {
                    continue;
                }

                items.Add(new ItemRule { Hotkey = hotkey, Slot = item.SlotNumber });
            }

            rules.Items = items.ToArray();

            var mappings = new List<MapRule>();
            foreach (KeyMapping mapping in _config.Input.Mappings)
            {
                if (!mapping.Enabled || string.IsNullOrWhiteSpace(mapping.From) || string.IsNullOrWhiteSpace(mapping.To))
                {
                    continue;
                }

                string originalKey = mapping.From.Trim();
                ParsedHotkey newKey = ParsedHotkey.Parse(mapping.To);
                if (!ParsedHotkey.Parse(originalKey).Valid || !newKey.Valid)
                {
                    continue;
                }

                mappings.Add(new MapRule { OriginalKey = originalKey, NewKey = newKey });
            }

            rules.Mappings = mappings.ToArray();

            var skillConverts = new List<SkillConvertRule>();
            if (_config.Input.SkillsEnabled)
            {
                for (int i = 0; i < _config.Input.Skills.Count; i++)
                {
                    SkillSlot slot = _config.Input.Skills[i];
                    if (string.IsNullOrWhiteSpace(slot.Key))
                    {
                        continue;
                    }

                    string key = slot.Key.Trim();
                    if (key.Length != 1 || !char.IsLetterOrDigit(key[0]))
                    {
                        continue;
                    }

                    ParsedHotkey hotkey = ParsedHotkey.Parse(key);
                    if (hotkey.Valid)
                    {
                        skillConverts.Add(new SkillConvertRule
                        {
                            Hotkey = hotkey
                        });
                    }
                }
            }

            rules.SkillConverts = skillConverts.ToArray();
            _rules = rules;
        }

        private bool OnHookKey(HookKeyEvent e)
        {
            RuleSet rules = _rules;
            if (rules == null || e.IsInjected)
            {
                return false;
            }

            if (!e.IsDown)
            {
                lock (_consumedLock)
                {
                    return _consumedKeys.Remove(e.VirtualKey);
                }
            }

            lock (_consumedLock)
            {
                if (_consumedKeys.Contains(e.VirtualKey))
                {
                    return true;
                }
            }

            if (rules.Panic.Matches(e))
            {
                Consume(e.VirtualKey);
                TogglePauseInternal(true);
                return true;
            }

            if (rules.Apply.Matches(e))
            {
                Consume(e.VirtualKey);
                _dispatcher.BeginInvoke(new Action(() =>
                {
                    if (_game == null)
                    {
                        SetMessage("未检测到游戏。");
                    }
                    else
                    {
                        _ = AttachNowAsync();
                    }
                }));
                return true;
            }

            if (_paused || _gamePid == 0 || !IsGameForeground())
            {
                return false;
            }

            // Chat state is native-only. It tracks every way of opening chat
            // (Enter, mouse click, script) and is already used by the Agent
            // for skill/camera conversion. Versions without a verified RVA
            // continue without chat protection until adapted separately.
            GameUiStateReader uiReader = _uiStateReader;
            if (uiReader != null && uiReader.ChatAvailable)
            {
                if (!uiReader.TryReadChat(out bool nativeChatOpen))
                {
                    // The flag address became unreadable (map teardown). Pass
                    // keys through rather than risking item/mapping actions
                    // while the player may be typing.
                    return false;
                }

                if (nativeChatOpen != _chatOpen)
                {
                    _chatOpen = nativeChatOpen;
                    _log.Info(nativeChatOpen
                        ? "聊天框状态: 打开（原生检测，改键暂停）"
                        : "聊天框状态: 关闭（原生检测，改键恢复）");
                }

                if (nativeChatOpen)
                {
                    return false;
                }
            }

            // Items and key mappings must only rewrite keys while a map is
            // actually running. The Agent's STATE ready flag follows the
            // in-game UI chain, so lobby/loading screens pass keys through
            // untouched (skills are gated separately by the command card in
            // the Agent).
            if (!_chatReady || _agentRef == null)
            {
                return false;
            }

            // F10/EscMenu hotkeys (M/S/L/O/H/T/E/R) belong to the menu's own
            // controls. This hook runs before the game's key dispatch, unlike
            // KK's command-card converter, so it sees those keys too. When the
            // game's active UI mode is the CEscMenu object, pass everything
            // through untouched.
            if (uiReader != null && uiReader.MenuAvailable)
            {
                if (!uiReader.TryReadMenu(out bool nativeMenuOpen))
                {
                    // Unknown menu state: do not consume a key that may be a
                    // menu shortcut.
                    return false;
                }

                if (nativeMenuOpen != _menuOpen)
                {
                    _menuOpen = nativeMenuOpen;
                    _log.Info(nativeMenuOpen
                        ? "菜单状态: 打开（原生检测，改键暂停）"
                        : "菜单状态: 关闭（原生检测，改键恢复）");
                }

                if (nativeMenuOpen)
                {
                    return false;
                }
            }

            if (_chatAvailable && _chatReady && _agentRef != null)
            {
                foreach (MacroRule macro in rules.Macros)
                {
                    if (!macro.Hotkey.Matches(e))
                    {
                        continue;
                    }

                    Consume(e.VirtualKey);
                    if (IsMacroCoolingDown(macro))
                    {
                        return true;
                    }

                    SendMacro(macro);
                    return true;
                }
            }

            if (_skillConvertActive && rules.SkillConverts.Length > 0)
            {
                foreach (SkillConvertRule skill in rules.SkillConverts)
                {
                    // Treat Shift like the game does: Shift+skill key must
                    // still reach the Agent for conversion, not fall through
                    // to an item or mapping rule bound to the same key.
                    if (!skill.Hotkey.Matches(e) && !skill.Hotkey.MatchesExtraShift(e))
                    {
                        continue;
                    }

                    // The Agent owns the actual command-card read, key
                    // substitution and quick-cast click at target mode.
                    return false;
                }
            }

            // Shift+D must use the same item/command as D: the physical Shift
            // stays down, so the game still treats the injected key as a
            // shifted order. Exact bindings win over this implicit variant.
            return TryItemRules(rules, e, false) ||
                   TryMappingRules(rules, e, false) ||
                   TryItemRules(rules, e, true) ||
                   TryMappingRules(rules, e, true);
        }

        private bool TryItemRules(RuleSet rules, HookKeyEvent e, bool allowExtraShift)
        {
            if (!rules.ItemsEnabled)
            {
                return false;
            }

            foreach (ItemRule item in rules.Items)
            {
                if (!MatchesHotkey(item.Hotkey, e, allowExtraShift))
                {
                    continue;
                }

                Consume(e.VirtualKey);
                KeySender.SendItemKey(item.Slot);
                return true;
            }

            return false;
        }

        private bool TryMappingRules(RuleSet rules, HookKeyEvent e, bool allowExtraShift)
        {
            if (!rules.MappingEnabled)
            {
                return false;
            }

            foreach (MapRule mapping in rules.Mappings)
            {
                // The user presses the configured new key; the game receives
                // its native/original key.
                if (!MatchesHotkey(mapping.NewKey, e, allowExtraShift))
                {
                    continue;
                }

                Consume(e.VirtualKey);
                KeySender.SendChord(mapping.OriginalKey);
                return true;
            }

            return false;
        }

        private static bool MatchesHotkey(ParsedHotkey hotkey, HookKeyEvent e, bool allowExtraShift)
        {
            return hotkey.Matches(e) || (allowExtraShift && hotkey.MatchesExtraShift(e));
        }

        private bool IsMacroCoolingDown(MacroRule macro)
        {
            long now = Environment.TickCount;
            lock (_macroCooldowns)
            {
                if (_macroCooldowns.TryGetValue(macro.Id, out long last) && unchecked(now - last) < macro.CooldownMs)
                {
                    return true;
                }

                _macroCooldowns[macro.Id] = now;
                return false;
            }
        }

        private async void SendMacro(MacroRule macro)
        {
            AgentClient agent = _agentRef;
            if (agent == null)
            {
                return;
            }

            try
            {
                AgentResponse response = await agent.SendChatAsync(macro.Text, macro.Channel).ConfigureAwait(false);
                if (response.Ok)
                {
                    _log.Info("快捷喊话发送成功(" + (macro.Channel == 0 ? "所有人" : "盟友") + "): " + macro.Text);
                }
                else
                {
                    _log.Warn("快捷喊话发送失败: " + response.Message);
                }

                _ = _dispatcher.BeginInvoke(new Action(() =>
                {
                    SetMessage(response.Ok ? "已发送喊话: " + macro.Text : "喊话失败: " + response.Message);
                }));
            }
            catch (Exception ex)
            {
                _log.Error("发送喊话异常", ex);
            }
        }

        private bool IsGameForeground()
        {
            int pid = _gamePid;
            if (pid == 0)
            {
                return false;
            }

            IntPtr foreground = NativeMethods.GetForegroundWindow();
            if (foreground == IntPtr.Zero)
            {
                return false;
            }

            NativeMethods.GetWindowThreadProcessId(foreground, out uint foregroundPid);
            return foregroundPid == (uint)pid;
        }

        private void Consume(int virtualKey)
        {
            lock (_consumedLock)
            {
                _consumedKeys.Add(virtualKey);
            }
        }

        private void TogglePauseInternal(bool fromHook)
        {
            bool paused;
            lock (_pauseLock)
            {
                _paused = !_paused;
                paused = _paused;
            }

            AgentClient agent = _agentRef;
            if (agent != null)
            {
                _ = SetAgentPausedAsync(agent, paused);
                if (_skillProfileApplied)
                {
                    _ = QueueSkillApply(agent, paused ? (bool?)false : null);
                }
            }

            if (fromHook)
            {
                _dispatcher.BeginInvoke(new Action(() => UpdatePauseUi(paused)));
            }
            else
            {
                UpdatePauseUi(paused);
            }
        }

        private void CloseUiStateReader()
        {
            _uiStateReader?.Dispose();
            _uiStateReader = null;
        }

        private async Task SetAgentPausedAsync(AgentClient agent, bool paused)
        {
            try
            {
                await agent.SetPausedAsync(paused).ConfigureAwait(false);
            }
            catch
            {
                // Best effort.
            }
        }

        private void UpdatePauseUi(bool paused)
        {
            _message = paused
                ? "已紧急暂停改键与喊话（Ctrl+Alt+F12 恢复）"
                : "已恢复改键与喊话";
            _log.Info(paused
                ? "紧急暂停已开启（仅停用改键与喊话，窗口/视距/状态条保持）。"
                : "紧急暂停已解除。");
            RaiseSnapshot();
        }

        private void SetError(string message)
        {
            _state = SessionState.Error;
            _message = message;
            _log.Error(message);
            RaiseSnapshot();
        }

        private void SetMessage(string message)
        {
            _message = message;
            RaiseSnapshot();
        }

        private void RaiseSnapshot()
        {
            if (_disposed)
            {
                return;
            }

            string version = _game == null ? string.Empty : _game.VersionText;
            var snapshot = new SessionSnapshot
            {
                State = _state,
                Pid = _game == null ? 0 : _game.Pid,
                Window = _game == null ? IntPtr.Zero : _game.Window,
                VersionText = version,
                VersionLabel = _game == null ? string.Empty : (_profile == null ? LabelFor(version) : _profile.DisplayName),
                Message = _message,
                Attached = _state == SessionState.Attached && _agent != null && _agent.Connected,
                Paused = _paused,
                NeedsRestart = _needsRestart
            };

            SnapshotChanged?.Invoke(snapshot);
        }

        private static bool IsProcessAlive(int pid)
        {
            IntPtr handle = NativeMethods.OpenProcess(
                NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION | NativeMethods.SYNCHRONIZE,
                false,
                pid);
            if (handle == IntPtr.Zero)
            {
                int error = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
                // ERROR_INVALID_PARAMETER means the PID is gone. Access denied means we
                // cannot tell; keep the session and let the attach step report the reason.
                return error != 87;
            }

            try
            {
                return NativeMethods.WaitForSingleObject(handle, 0) == 258; // WAIT_TIMEOUT
            }
            finally
            {
                NativeMethods.CloseHandle(handle);
            }
        }

        public static string LabelFor(string versionText)
        {
            if (string.IsNullOrWhiteSpace(versionText))
            {
                return "未知版本";
            }

            string version = versionText.Trim();
            if (version.StartsWith("1.24.4.", StringComparison.OrdinalIgnoreCase))
            {
                return "1.24e";
            }

            if (version.StartsWith("1.20.4.", StringComparison.OrdinalIgnoreCase))
            {
                return "1.20e";
            }

            if (version.StartsWith("1.27.0.", StringComparison.OrdinalIgnoreCase))
            {
                return "1.27a";
            }

            return version;
        }
    }
}
