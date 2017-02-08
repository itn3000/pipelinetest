using System;

namespace ConsoleApp
{
    using System.IO;
    using System.IO.Pipelines;
    using System.Linq;
    using System.Collections.Generic;
    static class EnumerableExtension
    {
        public static IPipeReader CreateFromEnumerable(this PipeFactory pfac, IEnumerable<int> data)
        {
            return pfac.CreateReader(null, async (r, w) =>
            {
                var tmp = new int[1];
                foreach (var b in data)
                {
                    var wbuf = w.Alloc();
                    try
                    {
                        tmp[0] = b;
                        var rspan = new Span<int>(tmp).AsBytes();
                        wbuf.Ensure(rspan.Length);
                        var span = wbuf.Memory.Span.Slice(0, rspan.Length);
                        rspan.CopyTo(span);
                        wbuf.Advance(rspan.Length);
                        await wbuf.FlushAsync();
                    }
                    catch (Exception e)
                    {
                        Console.WriteLine($"error in reader:{e}");
                        throw;
                    }
                }
                w.Complete();
            });
        }
    }
    class Program
    {
        static IPipeWriter Filter0xf0(PipeFactory pfac, IPipeWriter writer)
        {
            return pfac.CreateWriter(writer, async (r, w) =>
            {
                while (true)
                {
                    try
                    {
                        var wbuf = w.Alloc();
                        var result = await r.ReadAsync();
                        if (result.IsCancelled)
                        {
                            break;
                        }
                        if (result.IsCompleted && result.Buffer.IsEmpty)
                        {
                            break;
                        }
                        foreach (var rbuf in result.Buffer)
                        {
                            wbuf.Ensure(rbuf.Length);
                            var span = wbuf.Memory.Span;
                            rbuf.CopyTo(span.Slice(0, rbuf.Length));
                            for (int i = 0; i < span.Length; i++)
                            {
                                span[i] = (byte)(span[i] & 0x0f);
                            }
                            wbuf.Advance(rbuf.Length);
                            await wbuf.FlushAsync();
                        }
                        r.Advance(result.Buffer.End);
                    }
                    catch (Exception e)
                    {
                        Console.WriteLine($"{e}");
                    }
                }
            });
        }
        static void Main(string[] args)
        {
            using (var pfac = new PipeFactory())
            using (var ep = new MemoryStream())
            {
                var rw = pfac.Create();
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
                var writer = Filter0xf0(pfac, epwriter);
                var reader = pfac.CreateFromEnumerable(Enumerable.Range(0, 1000));
                reader.CopyToAsync(writer).Wait();
                // Console.WriteLine($"ep length = {ep.Length}");
                // var epar = ep.ToArray();
                // for (int i = 0; i < epar.Length; i++)
                // {
                //     Console.Write($"{epar[i].ToString("x2")}");
                //     if ((i & 0x1F) == 0x0F)
                //     {
                //         Console.WriteLine();
                //     }
                //     else
                //     {
                //         Console.Write(":");
                //     }
                // }
                // if ((epar.Length & 0x1f) != 0x10)
                // {
                //     Console.WriteLine();
                // }
            }
            Console.WriteLine("Hello World!");
        }
    }
}
