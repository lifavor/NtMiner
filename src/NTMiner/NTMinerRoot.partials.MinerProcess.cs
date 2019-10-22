﻿using NTMiner.Bus;
using NTMiner.Core;
using NTMiner.Core.Kernels;
using System;
using System.Collections;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace NTMiner {
    // ReSharper disable once InconsistentNaming
    public partial class NTMinerRoot : INTMinerRoot {
        private static class MinerProcess {
            #region CreateProcessAsync
            private static readonly object _locker = new object();
            public static void CreateProcessAsync(IMineContext mineContext) {
                Task.Factory.StartNew(() => {
                    lock (_locker) {
                        try {
#if DEBUG
                            Write.Stopwatch.Restart();
#endif
                            // 清理除当前外的Temp/Kernel
                            Cleaner.Instance.Clear();
#if DEBUG
                            Write.DevTimeSpan($"耗时{Write.Stopwatch.ElapsedMilliseconds}毫秒 {nameof(MinerProcess)}.{nameof(CreateProcessAsync)}[{nameof(Cleaner)}.{nameof(Cleaner.Clear)}]");
#endif
                            Write.UserOk("场地打扫完毕");
                            // 应用超频
                            if (Instance.GpuProfileSet.IsOverClockEnabled(mineContext.MainCoin.GetId())) {
                                Write.UserWarn("应用超频，如果CPU性能较差耗时可能超过1分钟，请耐心等待");
                                var cmd = new CoinOverClockCommand(mineContext.MainCoin.GetId());
                                // N卡超频当cpu性能非常差时较耗时，所以这里弄个回调
                                DelegatePath<CoinOverClockDoneEvent> callback = null;
                                callback = VirtualRoot.CreateEventPath<CoinOverClockDoneEvent>("超频完成后继续流程", LogEnum.DevConsole,
                                    message => {
                                        if (mineContext != Instance.CurrentMineContext) {
                                            VirtualRoot.DeletePath(callback);
                                        }
                                        else if (message.CmdId == cmd.Id) {
                                            VirtualRoot.DeletePath(callback);
                                            ContinueCreateProcess(mineContext);
                                        }
                                    });
                                VirtualRoot.Execute(cmd);
                            }
                            else {
                                ContinueCreateProcess(mineContext);
                            }
                        }
                        catch (Exception e) {
                            Logger.ErrorDebugLine(e);
                            Write.UserFail("挖矿内核启动失败，请联系开发人员解决");
                        }
                    }
                });
            }

            private static void ContinueCreateProcess(IMineContext mineContext) {
                Thread.Sleep(1000);
                if (mineContext != Instance.CurrentMineContext) {
                    Write.UserWarn("挖矿停止");
                    return;
                }
                // 解压内核包
                if (!mineContext.Kernel.ExtractPackage()) {
                    VirtualRoot.Happened(new StartingMineFailedEvent("内核解压失败，请卸载内核重试。"));
                }
                else {
                    Write.UserOk("内核包解压成功");
                }

                // 执行文件书写器
                mineContext.ExecuteFileWriters();

                // 分离命令名和参数
                GetCmdNameAndArguments(mineContext, out string kernelExeFileFullName, out string arguments);
                // 这是不应该发生的，如果发生很可能是填写命令的时候拼写错误了
                if (!File.Exists(kernelExeFileFullName)) {
                    Write.UserError(kernelExeFileFullName + "文件不存在，可能是小编拼写错误或是挖矿内核被杀毒软件删除导致，请退出杀毒软件重试或者QQ群联系小编。");
                }
                if (mineContext.KernelProcessType == KernelProcessType.Logfile) {
                    arguments = arguments.Replace("{logfile}", mineContext.LogFileFullName);
                }
                Write.UserOk($"\"{kernelExeFileFullName}\" {arguments}");
                Write.UserInfo($"有请内核上场：{mineContext.KernelProcessType}");
                if (mineContext != Instance.CurrentMineContext) {
                    Write.UserWarn("挖矿停止");
                    return;
                }
                switch (mineContext.KernelProcessType) {
                    case KernelProcessType.Logfile:
                        CreateLogfileProcess(mineContext, kernelExeFileFullName, arguments);
                        break;
                    case KernelProcessType.Pip:
                        CreatePipProcess(mineContext, kernelExeFileFullName, arguments);
                        break;
                    default:
                        throw new InvalidProgramException();
                }
                VirtualRoot.Happened(new MineStartedEvent(mineContext));
            }
            #endregion

            #region GetCmdNameAndArguments
            private static void GetCmdNameAndArguments(IMineContext mineContext, out string kernelExeFileFullName, out string arguments) {
                var kernel = mineContext.Kernel;
                if (string.IsNullOrEmpty(kernel.Package)) {
                    throw new InvalidDataException();
                }
                string kernelDir = Path.Combine(SpecialPath.KernelsDirFullName, Path.GetFileNameWithoutExtension(kernel.Package));
                string kernelCommandName = kernel.GetCommandName();
                kernelExeFileFullName = Path.Combine(kernelDir, kernelCommandName);
                if (!kernelExeFileFullName.EndsWith(".exe")) {
                    kernelExeFileFullName += ".exe";
                }
                var args = mineContext.CommandLine;
                arguments = args.Substring(kernelCommandName.Length).Trim();
            }
            #endregion

            #region KernelProcessDaemon
            private static DelegatePath<Per1MinuteEvent> _kernelProcessDaemon = null;
            private static void KernelProcessDaemon(IMineContext mineContext, Action clear) {
                if (_kernelProcessDaemon != null) {
                    VirtualRoot.DeletePath(_kernelProcessDaemon);
                    _kernelProcessDaemon = null;
                    clear?.Invoke();
                }
                string processName = mineContext.Kernel.GetProcessName();
                _kernelProcessDaemon = VirtualRoot.CreateEventPath<Per1MinuteEvent>("周期性检查挖矿内核是否消失，如果消失尝试重启", LogEnum.DevConsole,
                    action: message => {
                        if (mineContext == Instance.CurrentMineContext) {
                            if (!string.IsNullOrEmpty(processName)) {
                                Process[] processes = Process.GetProcessesByName(processName);
                                if (processes.Length == 0) {
                                    mineContext.AutoRestartKernelCount = mineContext.AutoRestartKernelCount + 1;
                                    Logger.ErrorWriteLine(processName + $"挖矿内核进程消失");
                                    if (Instance.MinerProfile.IsAutoRestartKernel && mineContext.AutoRestartKernelCount <= Instance.MinerProfile.AutoRestartKernelTimes) {
                                        Logger.WarnWriteLine($"尝试第{mineContext.AutoRestartKernelCount}次重启，共{Instance.MinerProfile.AutoRestartKernelTimes}次");
                                        Instance.RestartMine();
                                        Instance.CurrentMineContext.AutoRestartKernelCount = mineContext.AutoRestartKernelCount;
                                    }
                                    else {
                                        Instance.StopMineAsync(StopMineReason.KernelProcessLost);
                                    }
                                    if (_kernelProcessDaemon != null) {
                                        VirtualRoot.DeletePath(_kernelProcessDaemon);
                                        clear?.Invoke();
                                    }
                                }
                            }
                        }
                        else {
                            if (_kernelProcessDaemon != null) {
                                VirtualRoot.DeletePath(_kernelProcessDaemon);
                                _kernelProcessDaemon = null;
                                clear?.Invoke();
                            }
                        }
                    });
            }
            #endregion

            #region CreateLogfileProcess
            private static void CreateLogfileProcess(IMineContext mineContext, string kernelExeFileFullName, string arguments) {
                ProcessStartInfo startInfo = new ProcessStartInfo(kernelExeFileFullName, arguments) {
                    UseShellExecute = false,
                    CreateNoWindow = false,
                    WorkingDirectory = MainAssemblyInfo.TempDirFullName
                };
                // 追加环境变量
                foreach (var item in mineContext.CoinKernel.EnvironmentVariables) {
                    startInfo.EnvironmentVariables.Add(item.Key, item.Value);
                }
                Process process = new Process {
                    StartInfo = startInfo
                };
                process.Start();
                ReadPrintLoopLogFileAsync(mineContext, isWriteToConsole: false);
                KernelProcessDaemon(mineContext, null);
            }
            #endregion

            #region ReadPrintLoopLogFile
            private static void ReadPrintLoopLogFileAsync(IMineContext mineContext, bool isWriteToConsole) {
                Task.Factory.StartNew(() => {
                    bool isLogFileCreated = true;
                    int n = 0;
                    while (!File.Exists(mineContext.LogFileFullName)) {
                        if (n >= 20) {
                            // 20秒钟都没有建立日志文件，不可能
                            isLogFileCreated = false;
                            Write.UserFail("呃！意外，竟然20秒钟未产生内核输出，请联系开发人员解决。");
                            break;
                        }
                        Thread.Sleep(1000);
                        if (n == 0) {
                            Write.UserInfo("等待内核出场");
                        }
                        if (mineContext != Instance.CurrentMineContext) {
                            Write.UserWarn("挖矿上下文变更，结束内核输出等待。");
                            isLogFileCreated = false;
                            break;
                        }
                        n++;
                    }
                    if (isLogFileCreated) {
                        Write.UserOk("内核已上场，下面把舞台交给内核。");
                        StreamReader sreader = null;
                        try {
                            sreader = new StreamReader(File.Open(mineContext.LogFileFullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite), Encoding.Default);
                            while (mineContext == Instance.CurrentMineContext) {
                                string outline = sreader.ReadLine();
                                if (string.IsNullOrEmpty(outline) && sreader.EndOfStream) {
                                    Thread.Sleep(1000);
                                }
                                else {
                                    string input = outline;
                                    Guid kernelOutputId = Guid.Empty;
                                    if (mineContext.KernelOutput != null) {
                                        kernelOutputId = mineContext.KernelOutput.GetId();
                                    }
                                    Instance.KernelOutputFilterSet.Filter(kernelOutputId, ref input);
                                    ConsoleColor color = ConsoleColor.White;
                                    // 前译
                                    Instance.KernelOutputTranslaterSet.Translate(kernelOutputId, ref input, ref color, isPre: true);
                                    // 使用Claymore挖其非ETH币种时它也打印ETH，所以这里需要纠正它
                                    if ("Claymore".Equals(mineContext.Kernel.Code, StringComparison.OrdinalIgnoreCase)) {
                                        if (mineContext.MainCoin.Code != "ETH" && input.Contains("ETH")) {
                                            input = input.Replace("ETH", mineContext.MainCoin.Code);
                                        }
                                    }
                                    Instance.KernelOutputSet.Pick(ref input, mineContext);
                                    if (isWriteToConsole) {
                                        if (!string.IsNullOrEmpty(input)) {
                                            Write.UserLine(input, ConsoleColor.White);
                                        }
                                    }
                                }
                            }
                        }
                        catch (Exception e) {
                            Logger.ErrorDebugLine(e);
                        }
                        finally {
                            sreader?.Close();
                            sreader?.Dispose();
                        }
                        Write.UserWarn("内核表演结束");
                    }
                }, TaskCreationOptions.LongRunning);
            }
            #endregion

            #region CreatePipProcess
            // 创建管道，将输出通过管道转送到日志文件，然后读取日志文件内容打印到控制台
            private static void CreatePipProcess(IMineContext mineContext, string kernelExeFileFullName, string arguments) {
                SECURITY_ATTRIBUTES saAttr = new SECURITY_ATTRIBUTES {
                    bInheritHandle = true,
                    lpSecurityDescriptor = IntPtr.Zero,
                    length = Marshal.SizeOf(typeof(SECURITY_ATTRIBUTES))
                };

                //set the bInheritHandle flag so pipe handles are inherited

                saAttr.lpSecurityDescriptor = IntPtr.Zero;
                //get handle to current stdOut

                IntPtr mypointer = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(STARTUPINFO)));
                Marshal.StructureToPtr(saAttr, mypointer, true);
                var bret = CreatePipe(out var hReadOut, out var hWriteOut, mypointer, 0);
                //ensure the read handle to pipe for stdout is not inherited
                SetHandleInformation(hReadOut, HANDLE_FLAG_INHERIT, 0);
                ////Create pipe for the child process's STDIN
                STARTUPINFO lpStartupInfo = new STARTUPINFO {
                    cb = (uint)Marshal.SizeOf(typeof(STARTUPINFO)),
                    dwFlags = STARTF_USESTDHANDLES | STARTF_USESHOWWINDOW,
                    wShowWindow = SW_HIDE, // SW_HIDE; //SW_SHOW
                    hStdOutput = hWriteOut,
                    hStdError = hWriteOut,
                    hStdInput = IntPtr.Zero
                };
                StringBuilder lpEnvironment = new StringBuilder();
                // 复制父进程的环境变量
                IDictionary dic = Environment.GetEnvironmentVariables();
                // 追加环境变量
                foreach (var item in mineContext.CoinKernel.EnvironmentVariables) {
                    dic.Add(item.Key, item.Value);
                }
                foreach (var key in dic.Keys) {
                    if (key == null || key.ToString().Contains("\0")) {
                        continue;
                    }
                    var value = dic[key];
                    if (value == null || value.ToString().Contains("\0")) {
                        continue;
                    }
                    lpEnvironment.Append($"{key.ToString()}={value.ToString()}\0");
                }
                if (CreateProcess(
                    lpApplicationName: null,
                    lpCommandLine: new StringBuilder($"\"{kernelExeFileFullName}\" {arguments}"),
                    lpProcessAttributes: IntPtr.Zero,
                    lpThreadAttributes: IntPtr.Zero,
                    bInheritHandles: true,
                    dwCreationFlags: NORMAL_PRIORITY_CLASS,
                    lpEnvironment: lpEnvironment,
                    lpCurrentDirectory: Path.GetDirectoryName(kernelExeFileFullName),
                    lpStartupInfo: ref lpStartupInfo,
                    lpProcessInformation: out _)) {
                    if (bret == false) {
                        int lasterr = Marshal.GetLastWin32Error();
                        VirtualRoot.Happened(new StartingMineFailedEvent($"管道型进程创建失败 lasterr:{lasterr}"));
                    }
                    else {
                        Bus.DelegatePath<MineStopedEvent> closeHandle = null;
                        bool isHWriteOutHasClosed = false;
                        KernelProcessDaemon(mineContext, () => {
                            if (!isHWriteOutHasClosed) {
                                CloseHandle(hWriteOut);
                                isHWriteOutHasClosed = true;
                            }
                            VirtualRoot.DeletePath(closeHandle);
                        });
                        closeHandle = VirtualRoot.CreateEventPath<MineStopedEvent>("挖矿停止后关闭非托管的日志句柄", LogEnum.DevConsole,
                            action: message => {
                                // 挖矿停止后摘除挖矿内核进程守护器
                                if (_kernelProcessDaemon != null) {
                                    VirtualRoot.DeletePath(_kernelProcessDaemon);
                                    _kernelProcessDaemon = null;
                                }
                                if (!isHWriteOutHasClosed) {
                                    CloseHandle(hWriteOut);
                                    isHWriteOutHasClosed = true;
                                }
                                VirtualRoot.DeletePath(closeHandle);
                            });
                        Task.Factory.StartNew(() => {
                            using (FileStream fs = new FileStream(mineContext.LogFileFullName, FileMode.OpenOrCreate, FileAccess.ReadWrite)) {
                                const byte r = (byte)'\r';
                                byte[] buffer = new byte[1024];
                                int ret;
                                // Read会阻塞，直到读取到字符或者hWriteOut被关闭
                                while ((ret = Read(buffer, 0, buffer.Length, hReadOut)) > 0) {
                                    byte[] data = new byte[ret];
                                    int n = 0;
                                    for (int i = 0; i < ret; i++) {
                                        if (buffer[i] != r) {
                                            data[n] = buffer[i];
                                            n++;
                                        }
                                    }
                                    fs.Write(data, 0, n);
                                    fs.Flush();
                                }
                            }
                            CloseHandle(hReadOut);
                        }, TaskCreationOptions.LongRunning);
                        ReadPrintLoopLogFileAsync(mineContext, isWriteToConsole: true);
                    }
                }
                else {
                    VirtualRoot.Happened(new StartingMineFailedEvent($"内核启动失败，请重试"));
                }
            }

            private struct STARTUPINFO {
                public uint cb;
                public string lpReserved;
                public string lpDesktop;
                public string lpTitle;
                public uint dwX;
                public uint dwY;
                public uint dwXSize;
                public uint dwYSize;
                public uint dwXCountChars;
                public uint dwYCountChars;
                public uint dwFillAttribute;
                public uint dwFlags;
                public short wShowWindow;
                public short cbReserved2;
                public IntPtr lpReserved2;
                public IntPtr hStdInput;
                public IntPtr hStdOutput;
                public IntPtr hStdError;
            }

            private struct PROCESS_INFORMATION {
                public IntPtr hProcess;
                public IntPtr hThread;
                public uint dwProcessId;
                public uint dwThreadId;
            }

            private struct SECURITY_ATTRIBUTES {
                public int length;
                public IntPtr lpSecurityDescriptor;
                public bool bInheritHandle;
            }

            [DllImport("kernel32.dll")]
            private static extern int CloseHandle(IntPtr hObject);

            [DllImport("kernel32.dll")]
            private static extern bool CreatePipe(out IntPtr phReadPipe, out IntPtr phWritePipe, IntPtr lpPipeAttributes, uint nSize);

            [DllImport("kernel32.dll", SetLastError = true)]
            private static extern unsafe bool ReadFile(
                IntPtr hfile,
                void* pBuffer,
                int NumberOfBytesToRead,
                int* pNumberOfBytesRead,
                NativeOverlapped* lpOverlapped
            );

            /// <summary>
            /// 
            /// </summary>
            /// <param name="lpEnvironment">
            /// A pointer to the environment block for the new process. If this parameter is NULL, the new process uses the environment of the calling process.
            /// An environment block consists of a null-terminated block of null-terminated strings. Each string is in the following form:
            /// name=value\0
            /// Because the equal sign is used as a separator, it must not be used in the name of an environment variable.
            /// An environment block can contain either Unicode or ANSI characters. If the environment block pointed to by lpEnvironment contains Unicode characters, be sure that dwCreationFlags includes CREATE_UNICODE_ENVIRONMENT. If this parameter is NULL and the environment block of the parent process contains Unicode characters, you must also ensure that dwCreationFlags includes CREATE_UNICODE_ENVIRONMENT.
            /// The ANSI version of this function, CreateProcessA fails if the total size of the environment block for the process exceeds 32,767 characters.
            /// Note that an ANSI environment block is terminated by two zero bytes: one for the last string, one more to terminate the block. A Unicode environment block is terminated by four zero bytes: two for the last string, two more to terminate the block.
            /// A parent process can directly alter the environment variables of a child process during process creation. This is the only situation when a process can directly change the environment settings of another process. For more information, see Changing Environment Variables.
            /// 
            /// If an application provides an environment block, the current directory information of the system drives is not automatically propagated to the new process.
            /// For example, there is an environment variable named = C: whose value is the current directory on drive C.An application must manually pass the current directory 
            /// information to the new process.To do so, the application must explicitly create these environment variable strings, sort them alphabetically(because the system 
            /// uses a sorted environment), and put them into the environment block.Typically, they will go at the front of the environment block, due to the environment block sort order
            /// </param>
            /// <returns></returns>
            [DllImport("kernel32.dll")]
            private static extern bool CreateProcess(
                string lpApplicationName,
                StringBuilder lpCommandLine,
                IntPtr lpProcessAttributes,
                IntPtr lpThreadAttributes,
                bool bInheritHandles,
                uint dwCreationFlags,
                StringBuilder lpEnvironment,
                string lpCurrentDirectory,
                ref STARTUPINFO lpStartupInfo,
                out PROCESS_INFORMATION lpProcessInformation);

            [DllImport("kernel32.dll")]
            private static extern bool SetHandleInformation(IntPtr hObject, int dwMask, uint dwFlags);

            private static unsafe int Read(byte[] buffer, int index, int count, IntPtr hStdOut) {
                int n = 0;
                fixed (byte* p = buffer) {
                    if (!ReadFile(hStdOut, p + index, count, &n, (NativeOverlapped*)0))
                        return 0;
                }
                return n;
            }

            private const uint STARTF_USESHOWWINDOW = 0x00000001;
            private const uint STARTF_USESTDHANDLES = 0x00000100;
            private const uint STARTF_FORCEONFEEDBACK = 0x00000040;
            private const uint SF_USEPOSITION = 0x00000004;
            private const uint STARTF_USESIZE = 0x00000002;
            private const uint STARTF_USECOUNTCHARS = 0x00000008;
            private const uint NORMAL_PRIORITY_CLASS = 0x00000020;
            private const uint CREATE_BREAKAWAY_FROM_JOB = 0x01000000;
            private const uint CREATE_NO_WINDOW = 0x08000000;
            private const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400;
            private const short SW_SHOW = 5;
            private const short SW_HIDE = 0;
            private const int STD_OUTPUT_HANDLE = -11;
            private const int HANDLE_FLAG_INHERIT = 1;
            private const uint GENERIC_READ = 0x80000000;
            private const uint FILE_ATTRIBUTE_READONLY = 0x00000001;
            private const uint FILE_ATTRIBUTE_NORMAL = 0x00000080;
            private const int OPEN_EXISTING = 3;
            private const uint CREATE_NEW_CONSOLE = 0x00000010;
            private const uint STILL_ACTIVE = 0x00000103;
            #endregion
        }
    }
}
