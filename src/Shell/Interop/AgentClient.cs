using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using IWar3Key.Shell.Core;

namespace IWar3Key.Shell.Interop
{
    public sealed class AgentResponse
    {
        public bool Ok;
        public string[] Fields;
        public int Code;
        public string Message;

        public static AgentResponse Failure(string message, int code = -1)
        {
            return new AgentResponse { Ok = false, Code = code, Message = message, Fields = new string[0] };
        }

        public static AgentResponse FromFields(string[] fields)
        {
            if (fields == null || fields.Length == 0)
            {
                return Failure("Agent 返回空响应。");
            }

            var response = new AgentResponse
            {
                Fields = fields,
                Ok = !string.Equals(fields[0], "ERR", StringComparison.OrdinalIgnoreCase)
            };

            if (!response.Ok)
            {
                response.Code = fields.Length > 2 && int.TryParse(fields[2], out int code) ? code : -1;
                response.Message = fields.Length > 3 ? AgentClient.FromHexUnicode(fields[3]) : "Agent 返回错误。";
            }

            return response;
        }
    }

    public sealed class AgentInfo
    {
        public int Abi;
        public string GameVersion;
        public long GameBase;

        public static AgentInfo Parse(AgentResponse response)
        {
            string[] f = response.Fields;
            if (f == null || f.Length < 5)
            {
                return null;
            }

            return new AgentInfo
            {
                Abi = ParseInt(f[2]),
                GameVersion = AgentClient.FromHexUnicode(f[3]),
                GameBase = ParseLongHex(f[4])
            };
        }

        private static int ParseInt(string value)
        {
            return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int result) ? result : 0;
        }

        private static long ParseLongHex(string value)
        {
            return long.TryParse(value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out long result) ? result : 0;
        }
    }

    public sealed class AgentClient : IDisposable
    {
        public const int ProtocolAbi = 3;

        private readonly int _pid;
        private readonly string _pipeName;
        private readonly ConcurrentDictionary<int, TaskCompletionSource<string[]>> _pending =
            new ConcurrentDictionary<int, TaskCompletionSource<string[]>>();
        private readonly SemaphoreSlim _writeLock = new SemaphoreSlim(1, 1);
        private NamedPipeClientStream _pipe;
        private StreamReader _reader;
        private StreamWriter _writer;
        private Task _readTask;
        private int _sequence;
        private volatile bool _connected;
        private int _disconnectedRaised;
        private bool _disposed;

        public AgentClient(int pid)
        {
            _pid = pid;
            _pipeName = "iWar3Key.Agent." + pid;
        }

        public event Action Disconnected;

        public bool Connected => _connected;

        public int GamePid => _pid;

        public bool Connect(int timeoutMs)
        {
            var watch = Stopwatch.StartNew();
            while (!_disposed && watch.ElapsedMilliseconds < timeoutMs)
            {
                try
                {
                    var pipe = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
                    pipe.Connect(300);
                    _pipe = pipe;
                    _reader = new StreamReader(pipe, Encoding.ASCII, false, 4096, true);
                    _writer = new StreamWriter(pipe, Encoding.ASCII, 4096, true) { AutoFlush = true, NewLine = "\n" };
                    _connected = true;
                    _readTask = Task.Run((Func<Task>)ReadLoopAsync);
                    return true;
                }
                catch (TimeoutException)
                {
                    CleanupPipe();
                    Thread.Sleep(100);
                }
                catch (IOException)
                {
                    CleanupPipe();
                    Thread.Sleep(100);
                }
            }

            return false;
        }

        public async Task<AgentResponse> HelloAsync(int timeoutMs = 5000)
        {
            return await RequestAsync("HELLO", string.Empty, timeoutMs).ConfigureAwait(false);
        }

        public async Task<AgentResponse> SendProfileAsync(VersionProfile profile, int timeoutMs = 3000)
        {
            string args = string.Format(
                CultureInfo.InvariantCulture,
                "{0:X} {1:X} {2:X} {3:X} {4:X} {5:X}",
                profile.CguiGlobalRva,
                profile.ChatBarOffset,
                profile.TextObjectOffset,
                profile.TextBufferOffset,
                profile.IsChattingRva,
                profile.EscapeMenuOffset);
            return await RequestAsync("PROFILE", args, timeoutMs).ConfigureAwait(false);
        }

        public async Task<AgentResponse> SendSkillProfileAsync(
            long unitListRva, long cguiRva, int selectionOffset, int activeUnitOffset,
            long commandBarOffset, long commandGridOffset,
            long commandFrameButtonOffset, long commandButtonHotkeyOffset,
            int timeoutMs = 3000)
        {
            string args = string.Format(
                CultureInfo.InvariantCulture,
                "{0:X} {1:X} {2:X} {3:X} {4:X} {5:X} {6:X} {7:X}",
                unitListRva,
                cguiRva,
                selectionOffset,
                activeUnitOffset,
                commandBarOffset,
                commandGridOffset,
                commandFrameButtonOffset,
                commandButtonHotkeyOffset);
            return await RequestAsync("SKILLPROFILE", args, timeoutMs).ConfigureAwait(false);
        }

        public async Task<AgentResponse> SendSkillInfoAsync(bool enabled, int timeoutMs = 2000)
        {
            return await RequestAsync("SKILLINFO", enabled ? "1" : "0", timeoutMs).ConfigureAwait(false);
        }

        public async Task<AgentResponse> SendCameraProfileAsync(
            long setFieldRva,
            long cguiGlobalRva = 0, int chatBarOffset = 0,
            int textObjectOffset = 0, int textBufferOffset = 0,
            int timeoutMs = 3000)
        {
            string args = string.Format(
                CultureInfo.InvariantCulture,
                "{0:X} {1:X} {2:X} {3:X} {4:X}",
                setFieldRva,
                cguiGlobalRva,
                chatBarOffset,
                textObjectOffset,
                textBufferOffset);
            return await RequestAsync("CAMERAPROFILE", args, timeoutMs).ConfigureAwait(false);
        }

        public async Task<AgentResponse> SendCameraAsync(
            bool enabled, int distance, string zoomOutHotkey, string zoomInHotkey,
            int timeoutMs = 2000)
        {
            int zoomOutVk = 0;
            int zoomOutModifiers = 0;
            int zoomInVk = 0;
            int zoomInModifiers = 0;
            if (HotkeyUtil.TryParse(zoomOutHotkey, out HotkeyChord zoomOutChord))
            {
                zoomOutVk = zoomOutChord.VirtualKey;
                zoomOutModifiers = (int)zoomOutChord.Modifiers;
            }

            if (HotkeyUtil.TryParse(zoomInHotkey, out HotkeyChord zoomInChord))
            {
                zoomInVk = zoomInChord.VirtualKey;
                zoomInModifiers = (int)zoomInChord.Modifiers;
            }

            string args = string.Format(
                CultureInfo.InvariantCulture,
                "{0} {1} {2} {3} {4} {5}",
                enabled ? 1 : 0,
                distance,
                zoomOutVk,
                zoomOutModifiers,
                zoomInVk,
                zoomInModifiers);
            return await RequestAsync("CAMERA", args, timeoutMs).ConfigureAwait(false);
        }

        public async Task<AgentResponse> SendStatusBarProfileAsync(
            long allocCallRva, long updateCallRva, long originalUpdateRva,
            long ctorRva, long getUnitStateRva, long unitScaleRva,
            long setBarPositionRva, long setBarBoundsRva, long getUnitUIDefRva,
            long isUnitStructureRva, long getLocalPlayerRva, long getCPlayerWar3Rva,
            long cPlayerAttrRva, long integerGetValueRva, long cguiRva,
            int healthFlagOffset, int timeoutMs = 3000)
        {
            string args = string.Format(
                CultureInfo.InvariantCulture,
                "{0:X} {1:X} {2:X} {3:X} {4:X} {5:X} {6:X} {7:X} {8:X} {9:X} {10:X} {11:X} {12:X} {13:X} {14:X} {15:X}",
                allocCallRva,
                updateCallRva,
                originalUpdateRva,
                ctorRva,
                getUnitStateRva,
                unitScaleRva,
                setBarPositionRva,
                setBarBoundsRva,
                getUnitUIDefRva,
                isUnitStructureRva,
                getLocalPlayerRva,
                getCPlayerWar3Rva,
                cPlayerAttrRva,
                integerGetValueRva,
                cguiRva,
                healthFlagOffset);
            return await RequestAsync("STATUSBARPROFILE", args, timeoutMs).ConfigureAwait(false);
        }

        public async Task<AgentResponse> SendStatusBarAsync(
            bool health, bool friendlyMana, bool enemyMana, int timeoutMs = 2000)
        {
            string args = string.Format(
                CultureInfo.InvariantCulture,
                "{0} {1} {2}",
                health ? 1 : 0,
                friendlyMana ? 1 : 0,
                enemyMana ? 1 : 0);
            return await RequestAsync("STATUSBAR", args, timeoutMs).ConfigureAwait(false);
        }

        public async Task<AgentResponse> SendSkillsAsync(string[] keysBySlot,
                                                         string[] quickCastBySlot,
                                                         string[] itemQuickCastBySlot,
                                                         int timeoutMs = 3000)
        {
            var builder = new StringBuilder();
            for (int i = 0; i < 12; i++)
            {
                if (i != 0)
                {
                    builder.Append(' ');
                }

                builder.Append(keysBySlot != null && i < keysBySlot.Length && !string.IsNullOrEmpty(keysBySlot[i])
                    ? keysBySlot[i]
                    : "-");
            }

            for (int i = 0; i < 12; i++)
            {
                builder.Append(' ');
                builder.Append(quickCastBySlot != null && i < quickCastBySlot.Length &&
                               quickCastBySlot[i] == "1"
                    ? "1"
                    : "0");
            }

            for (int i = 0; i < 6; i++)
            {
                builder.Append(' ');
                builder.Append(itemQuickCastBySlot != null && i < itemQuickCastBySlot.Length &&
                               itemQuickCastBySlot[i] == "1"
                    ? "1"
                    : "0");
            }

            return await RequestAsync("SKILLS", builder.ToString(), timeoutMs).ConfigureAwait(false);
        }

        public async Task<AgentResponse> ConfigureAsync(int sendDelayMs, int timeoutMs = 3000)
        {
            string args = sendDelayMs.ToString(CultureInfo.InvariantCulture);
            return await RequestAsync("CONFIG", args, timeoutMs).ConfigureAwait(false);
        }

        public async Task<AgentResponse> SetPausedAsync(bool paused, int timeoutMs = 2000)
        {
            return await RequestAsync("PAUSE", paused ? "1" : "0", timeoutMs).ConfigureAwait(false);
        }

        public async Task<AgentResponse> GetStateAsync(int timeoutMs = 2000)
        {
            return await RequestAsync("STATE", string.Empty, timeoutMs).ConfigureAwait(false);
        }

        public async Task<AgentResponse> SendChatAsync(string text, int channel, int timeoutMs = 6000)
        {
            string args = channel.ToString(CultureInfo.InvariantCulture) + " " + ToHexUnicode(text);
            return await RequestAsync("CHAT", args, timeoutMs).ConfigureAwait(false);
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _connected = false;
            CleanupPipe();
            FailPending("Agent 连接已关闭。");
        }

        private async Task<AgentResponse> RequestAsync(string command, string args, int timeoutMs)
        {
            if (!_connected)
            {
                return AgentResponse.Failure("Agent 未连接。");
            }

            int seq = Interlocked.Increment(ref _sequence);
            var completion = new TaskCompletionSource<string[]>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending[seq] = completion;

            string line = string.IsNullOrEmpty(args)
                ? command + " " + seq.ToString(CultureInfo.InvariantCulture)
                : command + " " + seq.ToString(CultureInfo.InvariantCulture) + " " + args;

            try
            {
                await WriteLineAsync(line).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _pending.TryRemove(seq, out _);
                return AgentResponse.Failure("发送 Agent 命令失败: " + ex.Message);
            }

            Task completed = await Task.WhenAny(completion.Task, Task.Delay(timeoutMs)).ConfigureAwait(false);
            _pending.TryRemove(seq, out _);

            if (completed != completion.Task)
            {
                return AgentResponse.Failure("Agent 响应超时。");
            }

            return AgentResponse.FromFields(completion.Task.Result);
        }

        private async Task WriteLineAsync(string line)
        {
            if (!_connected || _writer == null)
            {
                throw new IOException("Agent 未连接。");
            }

            await _writeLock.WaitAsync().ConfigureAwait(false);
            try
            {
                await _writer.WriteLineAsync(line).ConfigureAwait(false);
            }
            finally
            {
                _writeLock.Release();
            }
        }

        private async Task ReadLoopAsync()
        {
            try
            {
                while (true)
                {
                    string line = await _reader.ReadLineAsync().ConfigureAwait(false);
                    if (line == null)
                    {
                        break;
                    }

                    line = line.TrimEnd('\r');
                    if (line.Length == 0)
                    {
                        continue;
                    }

                    string[] parts = line.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length < 2)
                    {
                        continue;
                    }

                    if (int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int seq) &&
                        _pending.TryRemove(seq, out TaskCompletionSource<string[]> completion))
                    {
                        completion.TrySetResult(parts);
                    }
                }
            }
            catch (Exception)
            {
                // Pipe closed or read failure.
            }
            finally
            {
                _connected = false;
                FailPending("Agent 连接断开。");
                if (Interlocked.Exchange(ref _disconnectedRaised, 1) == 0)
                {
                    Disconnected?.Invoke();
                }
            }
        }

        private void FailPending(string message)
        {
            foreach (var pair in _pending)
            {
                if (_pending.TryRemove(pair.Key, out TaskCompletionSource<string[]> completion))
                {
                    completion.TrySetResult(new[] { "ERR", pair.Key.ToString(CultureInfo.InvariantCulture), "-1", ToHexUnicode(message) });
                }
            }
        }

        private void CleanupPipe()
        {
            try { _writer?.Dispose(); } catch { }
            try { _reader?.Dispose(); } catch { }
            try { _pipe?.Dispose(); } catch { }
            _writer = null;
            _reader = null;
            _pipe = null;
        }

        public static string ToHexUnicode(string text)
        {
            byte[] bytes = Encoding.Unicode.GetBytes(text ?? string.Empty);
            var builder = new StringBuilder(bytes.Length * 2);
            foreach (byte b in bytes)
            {
                builder.Append(b.ToString("X2", CultureInfo.InvariantCulture));
            }

            return builder.ToString();
        }

        public static string FromHexUnicode(string hex)
        {
            if (string.IsNullOrEmpty(hex) || (hex.Length & 1) != 0)
            {
                return string.Empty;
            }

            byte[] bytes = new byte[hex.Length / 2];
            for (int i = 0; i < bytes.Length; i++)
            {
                if (!byte.TryParse(hex.Substring(i * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out bytes[i]))
                {
                    return string.Empty;
                }
            }

            return Encoding.Unicode.GetString(bytes);
        }
    }
}
