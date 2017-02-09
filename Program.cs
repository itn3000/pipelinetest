using System;

namespace ConsoleApp
{
    using System.Reactive;
    using System.Reactive.Linq;
    using System.IO;
    using System.IO.Pipelines;
    using System.Linq;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Threading.Tasks;
    using System.Text;
    using System.Threading;
    static class PipelineExtension
    {
        public static IPipeReader CreateFromCommand(this PipeFactory pfac, string filePath, string args
            , IPipeWriter stderr = null, IPipeReader stdin = null, CancellationToken ct = default(CancellationToken))
        {
            var si = new ProcessStartInfo(filePath, args);
            si.RedirectStandardOutput = true;
            si.RedirectStandardError = stderr != null;
            si.RedirectStandardInput = stdin != null;
            si.UseShellExecute = false;
            si.CreateNoWindow = true;
            return pfac.CreateReader(null, async (r, w) =>
            {
                try
                {
                    using (var proc = new Process())
                    {
                        proc.StartInfo = si;
                        proc.Start();
                        var procReader = proc.StandardOutput.BaseStream.AsPipelineReader();
                        var errReader = stderr != null ? proc.StandardError.BaseStream.AsPipelineReader() : null;
                        await Task.WhenAll(
                            Task.Run(async () =>
                            {
                                if (stdin != null)
                                {
                                    await stdin.CopyToAsync(proc.StandardInput.BaseStream).ConfigureAwait(false);
                                    proc.StandardInput.Dispose();
                                }
                            })
                            ,
                            Task.Run(async () =>
                            {
                                if (stderr != null)
                                {
                                    await errReader.CopyToAsync(stderr).ConfigureAwait(false);
                                }
                            })
                            ,
                            Task.Run(() =>
                            {
                                try
                                {
                                    using (ct.Register(() => proc.Kill()))
                                    {
                                        proc.WaitForExit();
                                    }
                                }
                                catch (Exception e)
                                {
                                    procReader.Complete(e);
                                    procReader.CancelPendingRead();
                                    if (errReader != null)
                                    {
                                        errReader.CancelPendingRead();
                                    }
                                }
                            })
                            ,
                            Task.Run(async () =>
                            {
                                await procReader.CopyToAsync(w).ConfigureAwait(false);
                            })
                        ).ConfigureAwait(false);
                        w.Complete();
                    }
                }
                catch (Exception e)
                {
                    Console.WriteLine($"{e}");
                    w.Complete(e);
                }
            });
        }
        /// <summary>create pipeline reader from IEnumerable<T></summary>
        public static IPipeReader CreateFromEnumerable<T>(this PipeFactory pfac, IEnumerable<T> data) where T : struct
        {
            return pfac.CreateReader(null, async (r, w) =>
            {
                var tmp = new T[1];
                foreach (var b in data)
                {
                    // allocating write buffer
                    var wbuf = w.Alloc();
                    try
                    {
                        tmp[0] = b;
                        var rspan = new Span<T>(tmp).AsBytes();
                        wbuf.Ensure(rspan.Length);
                        var span = wbuf.Memory.Span.Slice(0, rspan.Length);
                        rspan.CopyTo(span);
                        wbuf.Advance(rspan.Length);
                    }
                    catch (Exception e)
                    {
                        Console.WriteLine($"error in reader:{e}");
                        await wbuf.FlushAsync();
                        // inform exception to next reader
                        w.Complete(e);
                        // when exception is thrown to outside of function, pipe will be broken
                        return;
                    }
                    // must call FlushAsync after Advance
                    await wbuf.FlushAsync();
                }
                // if you forget to call Complete(), following pipeline reader may get stack
                w.Complete();
            });
        }
        // filtering test function for writer
        public static IPipeWriter Filter0xf0(this PipeFactory pfac, IPipeWriter writer)
        {
            return pfac.CreateWriter(writer, async (r, w) =>
            {
                while (true)
                {
                    var wbuf = w.Alloc();
                    try
                    {
                        var result = await r.ReadAsync();
                        if (result.IsCancelled)
                        {
                            // read cancelled
                            break;
                        }
                        if (result.IsCompleted && result.Buffer.IsEmpty)
                        {
                            // read end
                            break;
                        }
                        // ReadableBuffer is linked list of System.Memory<T>
                        foreach (var rbuf in result.Buffer)
                        {
                            // allocate buffer for writing
                            wbuf.Ensure(rbuf.Length);
                            // extract write buffer you want to use
                            var span = wbuf.Memory.Span.Slice(0, rbuf.Length);
                            // copy data to write buffer
                            // OutOfRangeException when wbuffer is shorter than read buffer
                            rbuf.CopyTo(span);
                            // change value of write buffer
                            for (int i = 0; i < span.Length; i++)
                            {
                                span[i] = (byte)(span[i] & 0x0f);
                            }
                            // send write buffer to next writer
                            wbuf.Advance(rbuf.Length);
                        }
                        // consume read buffer
                        // must call before next ReadAsync
                        r.Advance(result.Buffer.End);
                    }
                    catch (Exception e)
                    {
                        Console.WriteLine($"{e}");
                    }
                    // must call FlushAsync before next Alloc
                    await wbuf.FlushAsync();
                }
                w.Complete();
            });
        }
        public static IObservable<ReadOnlyMemory<byte>> CreateObservable(this PipeFactory pfac, IPipeReader reader)
        {
            return Observable.Create<ReadOnlyMemory<byte>>(async (ob, ct) =>
            {
                try
                {
                    while (true)
                    {
                        ct.ThrowIfCancellationRequested();
                        var result = await reader.ReadAsync();
                        if (result.IsCancelled || (result.IsCompleted && result.Buffer.IsEmpty))
                        {
                            break;
                        }
                        foreach (var rbuf in result.Buffer)
                        {
                            ob.OnNext(rbuf);
                        }
                        reader.Advance(result.Buffer.End);
                    }
                    ob.OnCompleted();
                }
                catch (Exception e)
                {
                    try
                    {
                        reader.CancelPendingRead();
                    }
                    catch
                    {

                    }
                    ob.OnError(e);
                }
            });
        }
    }
    class Program
    {
        static async Task ProcessPipeTest()
        {
            using (var pfac = new PipeFactory())
            using (var mstm = new MemoryStream())
            {
                var reader = pfac.CreateFromCommand(@"c:\Windows\System32\cmd.exe", "/c \"dir c:\\\"");
                await reader.CopyToAsync(mstm).ConfigureAwait(false);
                Console.WriteLine($"{Encoding.GetEncoding(932).GetString(mstm.ToArray())}");
            }
        }
        static void MemoryPipeTest()
        {
            using (var pfac = new PipeFactory())
            {
                // pipeline endpoint
                // if you want only to output Stream, call AsPipelineWriter(this Stream) extension method or CopyToAsync(Stream)
                var epwriter = pfac.CreateWriter(null, async (r, w) =>
                {
                    while (true)
                    {
                        try
                        {
                            var result = await r.ReadAsync();
                            if (result.IsCancelled || (result.IsCompleted && result.Buffer.IsEmpty))
                            {
                                break;
                            }
                            foreach (var rbuf in result.Buffer)
                            {
                                for (int i = 0; i < rbuf.Length; i++)
                                {
                                    Console.Write($"{rbuf.Span[i].ToString("x2")}");
                                    if ((i & 0xf) == 0xf)
                                    {
                                        Console.WriteLine();
                                    }
                                    else
                                    {
                                        Console.Write(":");
                                    }
                                }
                            }
                            r.Advance(result.Buffer.End);
                        }
                        catch (Exception e)
                        {
                            Console.WriteLine($"{e}");
                        }
                    }
                });
                var writer = pfac.Filter0xf0(epwriter);
                var reader = pfac.CreateFromEnumerable(Enumerable.Range(0, 10000));
                reader.CopyToAsync(writer).Wait();
            }
        }
        static void ObservableTest()
        {
            using(var pfac = new PipeFactory())
            {
                var r1 = pfac.CreateFromEnumerable(Enumerable.Range(0,10000));
                pfac.CreateObservable(r1)
                    .Subscribe(mem => {
                        Console.WriteLine($"mem={mem}");
                    },
                    e => {
                        Console.WriteLine($"onerror:{e}");
                    },
                    () => {
                        Console.WriteLine($"oncompleted");
                    });
            }
        }
        static void Main(string[] args)
        {
            // for cp932 encoding
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            ProcessPipeTest().Wait();
            MemoryPipeTest();
            ObservableTest();
            Console.WriteLine("Hello World!");
        }
    }
}
