using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace 通用模拟器
{
    /// <summary>日志类型</summary>
    public enum LogLevel
    {
        /// <summary>普通消息</summary>
        Info,
        /// <summary>提醒 / 警告</summary>
        Warning,
        /// <summary>抛出的异常 / 错误</summary>
        Error
    }

    /// <summary>一条日志记录</summary>
    public sealed class LogEntry
    {
        public DateTime Time { get; }
        public LogLevel Level { get; }
        public string Source { get; }
        public string Message { get; }
        public string LevelText => Level switch
        {
            LogLevel.Warning => "提醒",
            LogLevel.Error => "异常",
            _ => "消息"
        };

        public LogEntry(DateTime time, LogLevel level, string source, string message)
        {
            Time = time;
            Level = level;
            Source = source;
            Message = message;
        }

        public override string ToString() =>
            $"{Time:yyyy-MM-dd HH:mm:ss.fff} [{LevelText}] {Source} | {Message}";
    }

    /// <summary>
    /// 全局日志服务：写入先入内存队列，由单一后台线程每 200ms 批量落盘并批量同步到 UI，
    /// 避免高频日志时同步磁盘 IO + Dispatcher 洪泛拖慢从站响应。相同消息在窗口内限流。
    /// </summary>
    static class AppLogger
    {
        const int MaxEntries = 500;
        static readonly TimeSpan FlushInterval = TimeSpan.FromMilliseconds(200);
        static readonly TimeSpan DedupWindow = TimeSpan.FromSeconds(1);
        static readonly string logDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Modbus从站模拟器", "Logs");

        static readonly ConcurrentQueue<LogEntry> queue = new();
        static readonly ConcurrentDictionary<string, DateTime> lastLogged = new();
        static readonly CancellationTokenSource cts = new();
        static readonly Task writerTask;

        /// <summary>界面绑定的日志集合（最多保留最近 500 条）</summary>
        public static ObservableCollection<LogEntry> Entries { get; } = new();

        /// <summary>当前日志文件完整路径</summary>
        public static string CurrentLogFile => Path.Combine(logDir, $"log-{DateTime.Now:yyyyMMdd}.txt");

        static AppLogger()
        {
            writerTask = Task.Factory.StartNew(WriterLoop, cts.Token,
                TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }

        public static void Info(string message, [CallerMemberName] string source = "") =>
            Write(LogLevel.Info, message, source);

        public static void Warning(string message, [CallerMemberName] string source = "") =>
            Write(LogLevel.Warning, message, source);

        public static void Error(string message, [CallerMemberName] string source = "") =>
            Write(LogLevel.Error, message, source);

        /// <summary>记录异常（同时保留异常类型与堆栈）</summary>
        public static void Exception(string message, Exception ex, [CallerMemberName] string source = "") =>
            Write(LogLevel.Error, $"{message} | {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}", source);

        static void Write(LogLevel level, string message, string source)
        {
            if (string.IsNullOrEmpty(message)) return;

            // 去重限流：同一级别+方法+消息在窗口内只记一条，避免错误刷屏
            var key = $"{(int)level}|{source}|{message}";
            var now = DateTime.Now;
            if (lastLogged.TryGetValue(key, out var last) && now - last < DedupWindow)
                return;
            lastLogged[key] = now;
            // 控制去重表大小，避免长期运行无限增长
            if (lastLogged.Count > 2000) lastLogged.Clear();

            queue.Enqueue(new LogEntry(now, level, source ?? "", message));
        }

        static async Task WriterLoop()
        {
            var token = cts.Token;
            var batch = new List<LogEntry>();
            while (!token.IsCancellationRequested)
            {
                try { await Task.Delay(FlushInterval, token).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }

                Drain(batch);
                if (batch.Count == 0) continue;
                FlushBatch(batch);
                batch.Clear();
            }
            // 退出前把残留刷盘
            Drain(batch);
            if (batch.Count > 0) FlushBatch(batch);
        }

        static void Drain(List<LogEntry> batch)
        {
            while (batch.Count < 1000 && queue.TryDequeue(out var entry)) batch.Add(entry);
        }

        static void FlushBatch(IReadOnlyList<LogEntry> batch)
        {
            // 一次 open/write/close 写完整批
            try
            {
                Directory.CreateDirectory(logDir);
                var sb = new StringBuilder();
                foreach (var e in batch) sb.Append(e).Append(Environment.NewLine);
                File.AppendAllText(CurrentLogFile, sb.ToString(), Encoding.UTF8);
            }
            catch
            {
                // 日志写入失败不能影响主流程
            }

            // 批量同步到 UI：投递不可变快照，不能把后台仍在复用的 batch 引用交给 UI，
            // 否则 UI 枚举时后台 Drain/Clear 会抛“集合已修改”
            var app = Application.Current;
            if (app is null) return;
            var dispatcher = app.Dispatcher;
            var snapshot = batch.ToArray();
            Action append = () => AppendBatch(snapshot);
            if (dispatcher.CheckAccess()) append();
            else dispatcher.BeginInvoke(append);
        }

        static void AppendBatch(IReadOnlyList<LogEntry> batch)
        {
            foreach (var e in batch) Entries.Add(e);
            while (Entries.Count > MaxEntries) Entries.RemoveAt(0);
        }

        /// <summary>退出时刷盘：等待后台写线程把队列处理完</summary>
        public static void Shutdown()
        {
            cts.Cancel();
            try { writerTask.Wait(TimeSpan.FromSeconds(2)); } catch { }
        }

        public static void Clear() => Entries.Clear();
    }
}
