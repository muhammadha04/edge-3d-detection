// One background thread that encodes and writes everything the recorder produces (JPEG frames, depth PNGs,
// JSONL lines, CSV rows), so the render thread never waits on disk or compression. Bounded queue: when it is
// full the job is refused and the caller counts a dropped frame.
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;

namespace QuestDetect3D.Recording
{
    public sealed class RecorderWorker : IDisposable
    {
        private readonly BlockingCollection<Action> m_queue;
        private readonly Thread m_thread;
        private readonly Dictionary<string, StreamWriter> m_writers = new Dictionary<string, StreamWriter>();
        private long m_bytesWritten;
        private int m_errors;
        private string m_lastError;

        public RecorderWorker(int capacity = 64)
        {
            m_queue = new BlockingCollection<Action>(capacity);
            m_thread = new Thread(Loop) { IsBackground = true, Name = "QD3D recorder", Priority = ThreadPriority.BelowNormal };
            m_thread.Start();
        }

        public int Pending => m_queue.Count;
        public long BytesWritten => Interlocked.Read(ref m_bytesWritten);
        public int Errors => m_errors;
        public string LastError => m_lastError;

        /// <summary>False when the queue is full (caller drops the item).</summary>
        public bool TryEnqueue(Action job)
        {
            try
            {
                return !m_queue.IsAddingCompleted && m_queue.TryAdd(job);
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }

        /// <summary>Text lines always go in (blocking briefly if needed): the audit trail must not lose lines.</summary>
        public void AppendLine(string path, string line)
        {
            Action job = () =>
            {
                var w = Writer(path);
                w.Write(line);
                w.Write('\n');
                Interlocked.Add(ref m_bytesWritten, Encoding.UTF8.GetByteCount(line) + 1);
            };
            if (!TryEnqueue(job))
            {
                try
                {
                    m_queue.Add(job);
                }
                catch (InvalidOperationException)
                {
                    // shutting down
                }
            }
        }

        /// <summary>Only from inside a job (worker thread): append a line right away.</summary>
        public void AppendLineInJob(string path, string line)
        {
            var w = Writer(path);
            w.Write(line);
            w.Write('\n');
            Interlocked.Add(ref m_bytesWritten, Encoding.UTF8.GetByteCount(line) + 1);
        }

        public void WriteFile(string path, byte[] bytes)
        {
            File.WriteAllBytes(path, bytes);
            Interlocked.Add(ref m_bytesWritten, bytes.Length);
        }

        private StreamWriter Writer(string path)
        {
            if (!m_writers.TryGetValue(path, out var w))
            {
                w = new StreamWriter(path, true, new UTF8Encoding(false), 1 << 16);
                m_writers[path] = w;
            }

            return w;
        }

        private void Loop()
        {
            var lastFlush = DateTime.UtcNow;
            while (true)
            {
                Action job;
                try
                {
                    if (!m_queue.TryTake(out job, 500))
                    {
                        if (m_queue.IsCompleted)
                        {
                            break;
                        }

                        FlushAll();
                        lastFlush = DateTime.UtcNow;
                        continue;
                    }
                }
                catch (InvalidOperationException)
                {
                    break;
                }

                try
                {
                    job();
                }
                catch (Exception e)
                {
                    m_errors++;
                    m_lastError = e.GetType().Name + ": " + e.Message;
                }

                if ((DateTime.UtcNow - lastFlush).TotalSeconds > 1.0)
                {
                    FlushAll();
                    lastFlush = DateTime.UtcNow;
                }
            }

            FlushAll();
            foreach (var w in m_writers.Values)
            {
                try
                {
                    w.Dispose();
                }
                catch (Exception)
                {
                    // ignore
                }
            }

            m_writers.Clear();
        }

        private void FlushAll()
        {
            foreach (var w in m_writers.Values)
            {
                try
                {
                    w.Flush();
                }
                catch (Exception e)
                {
                    m_errors++;
                    m_lastError = e.Message;
                }
            }
        }

        /// <summary>Finish all queued work and close the files (waits up to <paramref name="timeoutMs"/>).</summary>
        public bool Stop(int timeoutMs = 20000)
        {
            m_queue.CompleteAdding();
            return m_thread.Join(timeoutMs);
        }

        public void Dispose()
        {
            if (!m_queue.IsAddingCompleted)
            {
                Stop();
            }

            m_queue.Dispose();
        }
    }
}
