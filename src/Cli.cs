using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace MdlOptimizer
{
    static class Cli
    {
        static string Usage { get { return "MDL Optimizer " + Optimizer.Version + Environment.NewLine + L.T("cli.usage"); } }

        public static int Main(string[] args)
        {
            L.ApplyUICulture();
            Console.OutputEncoding=new UTF8Encoding(false);
            int stopped=0;
            Console.CancelKeyPress+=(sender,e)=> { e.Cancel=true; Interlocked.Exchange(ref stopped,1); };
            Func<bool> cancel=()=>Interlocked.CompareExchange(ref stopped,0,0)!=0;
            try {
                if (args.Length > 0 && args[0] == "--lang") {
                    string language = args.Length > 1 ? args[1].ToLowerInvariant() : "";
                    switch (language) {
                        case "system": L.UseForSession(""); break;
                        case "pt-br": L.UseForSession("pt-BR"); break;
                        case "en": case "es": case "ru": L.UseForSession(language); break;
                        default: throw new ArgumentException(L.T("cli.language"));
                    }
                    args = args.Skip(2).ToArray();
                }
                if (args.Length==0 || args[0]=="--help" || args[0]=="-h") { Console.WriteLine(Usage); return 0; }
                string cmd=args[0].ToLowerInvariant(), output=null,preview=null;
                var files=new List<string>();
                var opts=new RunOptions();
                bool strict=false;
                for (int i=1;i<args.Length;i++) {
                    switch (args[i]) {
                        case "--out": if (++i>=args.Length) throw new ArgumentException(L.T("cli.missingPath","--out")); output=args[i]; break;
                        case "--preview": if (++i>=args.Length) throw new ArgumentException(L.T("cli.missingPath","--preview")); preview=args[i]; break;
                        case "--fast": opts.Thorough=false; break;
                        case "--strict": strict=true; break;
                        case "--exact-only": opts.ExactOnly=true; break;
                        default:
                            if (args[i].StartsWith("--")) throw new ArgumentException(L.T("cli.unknownOption",args[i]));
                            files.Add(args[i]); break;
                    }
                }
                if(strict && (cmd!="verify" || !opts.Thorough))
                    throw new ArgumentException(L.T("cli.strict"));
                if (cmd=="info" && files.Count==1) {
                    var s=Studio.Load(files[0]);
                    Console.WriteLine(Optimizer.Json(new { file=DiskPath.Display(DiskPath.Full(files[0])),sha256=Optimizer.HashFile(files[0]),
                        triangles=Optimizer.Count(s),default_body_triangles=Optimizer.DefaultCount(s),bones=s.Bones.Count,
                        sequences=s.Sequences.Count,bodyparts=s.Parts.Count,skins=s.Skins.Length,textures=s.Textures.Count }));
                    return 0;
                }
                if (cmd=="verify" && files.Count==2) {
                    if (preview!=null && (File.Exists(DiskPath.Full(preview)) || Directory.Exists(DiskPath.Full(preview)))) throw new IOException(L.T("cli.previewExists"));
                    var s=Studio.Load(files[0]); var other=Studio.Load(files[1]); s.VerifyMetadata(other);
                    var audit=new VisualAudit(s,opts.Thorough);
                    var v=audit.CompareSaved(other,cancel,null);
                    var detail=VisualAudit.DetailSweep(s,opts.Thorough).CompareSaved(other,cancel,null);
                    var reserved=opts.Thorough?VisualAudit.Reserved(s).CompareSaved(other,cancel,null):null;
                    bool passed=v.Passed && detail.Passed && (!strict || reserved.Passed);
                    if(preview!=null) {
                        var evidence=v;
                        if(v.Passed && !detail.Passed)evidence=detail;
                        else if(v.Passed && reserved!=null && !reserved.Passed)evidence=reserved;
                        evidence.SavePreview(preview);
                    }
                    bool exactPixels=v.ExactMatch && detail.ExactMatch && (reserved==null || reserved.ExactMatch);
                    Console.WriteLine(Optimizer.Json(new { passed=passed,metadata_verified=true,exact_pixels=exactPixels,
                        triangles=Optimizer.Count(other),views=v.Views,silhouette_iou=v.Silhouette,mean_rgb_error_255=v.MeanColor,
                        worst_view=v.WorstView,worst_silhouette_iou=v.WorstSilhouette,detail=new{passed=detail.Passed,views=detail.Views,
                        worst_view=detail.WorstView,silhouette_iou=detail.Silhouette,mean_rgb_error_255=detail.MeanColor},
                        reserved=reserved==null?null:(object)new{passed=reserved.Passed,failed_views=reserved.FailedViews,views=reserved.Views,
                            mean_rgb_error_255=reserved.MeanColor,silhouette_iou=reserved.Silhouette,worst_view=reserved.WorstView} }));
                    return passed?0:3;
                }
                if (cmd!="optimize" || files.Count==0 || String.IsNullOrWhiteSpace(output))
                    throw new ArgumentException(Usage);
                var results=new List<object>(); bool anyError=false;
                foreach (var f in files) {
                    string last=null; int lastPct=-10;
                    try {
                        var run=Optimizer.Run(f,output,opts,cancel,(pct,msg)=> {
                            if (pct>=lastPct+5 || (last!=msg && (pct<5 || pct>=94))) {
                                Console.Error.WriteLine("{0,3}%  {1}  {2}",pct,Path.GetFileName(f),msg); lastPct=pct; last=msg;
                            }
                        });
                        results.Add(new { input=run.Input,output=run.Directory,original=run.OriginalTriangles,
                            exact=run.ExactTriangles,game=run.GameFile==null ? (int?)null : run.GameTriangles,seconds=run.Seconds,
                            reserved=run.ReservedPassed.HasValue?(object)new{passed=run.ReservedPassed.Value,
                                failed_views=run.ReservedFailedViews,views=run.ReservedViews}:null });
                    } catch (OperationCanceledException) { throw; }
                    catch (Exception e) { anyError=true;results.Add(new { input=f,error=e.Message });Console.Error.WriteLine(f+": "+e.Message); }
                }
                Console.WriteLine(Optimizer.Json(results));
                return anyError?1:0;
            } catch (OperationCanceledException) { Console.Error.WriteLine(L.T("cli.cancelled"));return 130; }
            catch (Exception e) { Console.Error.WriteLine(e.Message); return 2; }
        }
    }
}
