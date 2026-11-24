using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace MdlOptimizer
{
    public sealed class RunOptions
    {
        public bool Thorough = true;
        public bool ExactOnly;
    }

    public sealed class RunResult
    {
        public string Input, Directory, ExactFile, GameFile, WorstView, BodyView;
        public int OriginalTriangles, ExactTriangles, GameTriangles, Views;
        public double Silhouette, ColorError, Seconds;
        public bool UsedExact;
        public bool? ReservedPassed;
        public int ReservedViews,ReservedFailedViews;
        internal LocalizedText WorstViewLabel, BodyViewLabel;

        public string DisplayView(bool wholeBody)
        {
            var label = wholeBody ? BodyViewLabel : WorstViewLabel;
            return label != null ? label.ToString() : (wholeBody ? BodyView : WorstView);
        }
    }

    public static class Optimizer
    {
        public const string Version = "1.1.0";
        public static IEnumerable<Mesh> Meshes(Studio s)
        {
            return s.Parts.SelectMany(p => p.Models).SelectMany(m => m.Meshes);
        }
        public static int Count(Studio s) { return Meshes(s).Sum(m => m.Faces.Count); }
        // Count the drawn combination with body=0, independently of optional submodels.
        public static int DefaultCount(Studio s)
        {
            return s.Parts.Sum(p => p.Models[0].Meshes.Sum(m => m.Faces.Count));
        }
        static int Count(Dictionary<Mesh,List<Tri>> d) { return d.Values.Sum(x => x.Count); }
        public static string HashFile(string path)
        {
            using (var sha = SHA256.Create())
            using (var f = File.OpenRead(DiskPath.Full(path)))
                return BitConverter.ToString(sha.ComputeHash(f)).Replace("-", "").ToLowerInvariant();
        }
        public static string Json(object value)
        {
            return new JavaScriptSerializer { MaxJsonLength = Int32.MaxValue }.Serialize(value);
        }
        static string Stem(string name)
        {
            var sb = new StringBuilder();
            foreach (char c in name.ToLowerInvariant()) {
                if (sb.Length == 24) break;
                if (c >= 'a' && c <= 'z' || c >= '0' && c <= '9' || c == '-' || c == '_') sb.Append(c);
                else if (sb.Length > 0 && sb[sb.Length - 1] != '_') sb.Append('_');
            }
            return sb.Length == 0 ? "modelo" : sb.ToString();
        }
        static string ModelPath(string staging,string name)
        {
            string folder = DiskPath.Full(Path.Combine(staging,name));
            Directory.CreateDirectory(folder);
            return DiskPath.Full(Path.Combine(folder,name+".mdl"));
        }
        static string Publish(string staging,string parent,string stem)
        {
            for (int i=1; ; i++) {
                string path=DiskPath.Full(Path.Combine(parent, i==1 ? stem : stem+"-"+i));
                if (File.Exists(path) || Directory.Exists(path)) continue;
                for(int retry=0;;retry++) {
                    try { Directory.Move(staging,path); return path; }
                    catch (Exception e) {
                        if (!(e is IOException) && !(e is UnauthorizedAccessException)) throw;
                        if(Directory.Exists(path) || File.Exists(path))break;
                        int code=e.HResult & 65535;
                        if(retry>=6 || (code!=5 && code!=32 && code!=33))throw;
                        // Sync clients and scanners can briefly hold a handle after the PNG and MDL close.
                        Thread.Sleep(40*(1<<retry));
                    }
                }
            }
        }
        internal static object Metrics(VisualResult v)
        {
            return new { passed=v.Passed, exact_pixels=v.ExactMatch, views=v.Views,failed_views=v.FailedViews,
                silhouette_iou=v.Silhouette, worst_silhouette_iou=v.WorstSilhouette,
                mean_rgb_error_255=v.MeanColor, worst_view=v.WorstView };
        }
        sealed class Variant
        {
            public LocalizedText Name;
            public int Level, N;
            public Dictionary<Mesh,List<Tri>> Faces;
            public bool Hybrid;
            public int Repairs;
            public Dictionary<Mesh,HashSet<int>> Locks;
        }
        public static RunResult Run(string input,string output,RunOptions options,
            Func<bool> cancelled,Action<int,string> notify)
        {
            return RunLocalized(input,output,options,cancelled,notify == null ? (Action<int,LocalizedText>)null :
                ((pct, message) => notify(pct, message.ToString())));
        }

        public static RunResult RunLocalized(string input,string output,RunOptions options,
            Func<bool> cancelled,Action<int,LocalizedText> notify)
        {
            if (String.IsNullOrWhiteSpace(output)) {
                var message = new LocalizedText("Core.ChooseOutput");
                throw message.Attach(new ArgumentException(message.ToString()));
            }
            string path=DiskPath.Full(input), parent=DiskPath.Full(output);
            var watch=Stopwatch.StartNew();
            var stop=cancelled ?? (() => false);
            Action check=delegate { if (stop()) throw new OperationCanceledException(); };
            int maxPercent=0;
            Action<int,LocalizedText> progress=delegate(int pct,LocalizedText msg) {
                maxPercent=Math.Max(maxPercent,Math.Min(100,pct));
                if (notify!=null) notify(maxPercent,msg);
            };
            check(); progress(0,new LocalizedText("Progress.Read"));
            string originalHash=HashFile(path);
            var source=Studio.Load(path);
            var companions=source.CompanionPaths.Select(f=>new { file=Path.GetFileName(f),sha256=HashFile(f) }).ToArray();
            string stem=Stem(Path.GetFileNameWithoutExtension(path));
            Directory.CreateDirectory(parent);
            string staging=DiskPath.Full(Path.Combine(parent,".mdlopt-"+Guid.NewGuid().ToString("N")));
            Directory.CreateDirectory(staging);
            bool published=false;
            try {
                int original=Count(source);
                progress(2,new LocalizedText("Progress.RemoveFaces"));
                var exact=Simplifier.Exact(source);
                string exName=stem+"_exato", gameName=stem+"_opt";
                string exFile=ModelPath(staging,exName);
                source.Save(exFile,exact,true,exName+".mdl");
                var exactDisk=Studio.Load(exFile);
                source.VerifyMetadata(exactDisk);
                if (Count(exactDisk)!=Count(exact)) throw LocalizedText.InvalidData("Core.ExactCountMismatch");

                progress(5,new LocalizedText("Progress.CompareExact"));
                var audit=new VisualAudit(source,options.Thorough);
                var detail=VisualAudit.DetailSweep(source,options.Thorough);
                var quick=options.Thorough ? new VisualAudit(source,false) : audit;
                var exactView=audit.CompareSaved(exactDisk,stop,(i,n)=>progress(5+20*i/Math.Max(n,1),new LocalizedText("Progress.ValidateExact")));
                var exactDetail=detail.CompareSaved(exactDisk,stop,null);
                bool exactFallback=false;
                if (!exactView.ExactMatch || !exactDetail.ExactMatch) {
                    // Raster ties from two coincident opaque surfaces can depend on their original order.
                    // Do not describe a cleanup as exact if this renderer observes a difference.
                    exactFallback=true;
                    var unchanged=Meshes(source).ToDictionary(m=>m,m=>m.Faces);
                    exact=unchanged;
                    File.Delete(exFile);
                    source.Save(exFile,unchanged,true,exName+".mdl");
                    exactDisk=Studio.Load(exFile);
                    source.VerifyMetadata(exactDisk);
                    exactView=audit.CompareSaved(exactDisk,stop,(i,n)=>progress(25,new LocalizedText("Progress.CheckGeometry")));
                    exactDetail=detail.CompareSaved(exactDisk,stop,null);
                    if (!exactView.ExactMatch || !exactDetail.ExactMatch) throw LocalizedText.InvalidData("Core.RenderMismatch");
                }
                int exactCount=Count(exactDisk), gameCount=exactCount;
                var finalView=exactView;var finalDetail=exactDetail;
                object qualityBudget=null;
                var trials=new List<object>();
                string selected="exato", gameFile=null;
                int visibleFaces=0;
                int gameDefault=DefaultCount(exactDisk);
                if (!options.ExactOnly) {
                    var bestFaces=exact;
                    progress(26,new LocalizedText("Progress.Simplify"));
                    var levels=new Dictionary<int,Dictionary<Mesh,List<Tri>>>();
                    levels[0]=exact;
                    var queue=new List<Variant>();
                    for (int level=1;level<=Simplifier.ModeCount;level++) {
                        check();
                        var faces=Simplifier.Reduce(source,exact,level,new HashSet<Mesh>());
                        levels[level]=faces;
                        int n=Count(faces);
                        if (n<exactCount) queue.Add(new Variant { Name=Simplifier.ProfileLabel(level),Level=level,N=n,Faces=faces });
                    }
                    int attempt=0;
                    gameFile=ModelPath(staging,gameName);
                    while (queue.Count>0) {
                        check();
                        queue.Sort((a,b)=>a.N!=b.N ? a.N.CompareTo(b.N) : b.Level.CompareTo(a.Level));
                        var variant=queue[0]; queue.RemoveAt(0);
                        if (variant.N>=gameCount) continue;
                        attempt++;
                        int stage=31+Math.Min(40,attempt*4);
                        var label=new LocalizedText("Progress.Triangles",variant.Name,variant.N);
                        var screening=new LocalizedText("Progress.Screen",label);
                        progress(stage,screening);
                        var tested=quick.Compare(variant.Faces,stop,(i,n)=>progress(stage,screening));
                        VisualResult full=null;
                        if (tested.Passed) {
                            progress(stage+2,new LocalizedText("Progress.VerifyExport",label));
                            if (File.Exists(gameFile)) File.Delete(gameFile);
                            source.Save(gameFile,variant.Faces,false,gameName+".mdl");
                            var exported=Studio.Load(gameFile);
                            source.VerifyMetadata(exported);
                            if (Count(exported)!=variant.N) throw LocalizedText.InvalidData("Core.ExportCountMismatch");
                            full=audit.CompareSaved(exported,stop,(i,n)=>progress(stage+2,new LocalizedText("Progress.FinalTest",label,i,n)));
                            if (full.Passed) {
                                selected=variant.Name.StableText; gameCount=variant.N; gameDefault=DefaultCount(exported); finalView=full; bestFaces=variant.Faces;
                            }
                        }
                        trials.Add(new { profile=variant.Name.StableText,triangles=variant.N,screen=Metrics(tested),export=full==null ? null : Metrics(full) });
                        if (gameCount<exactCount) break;
                        // Restore the region identified by the rendered error, even on a single-material model.
                        var failed=full ?? tested;
                        if ((!variant.Hybrid || variant.Locks!=null) && variant.Repairs<4 && failed.SuspectVertices.Count>0) {
                            var locks=new Dictionary<Mesh,HashSet<int>>();
                            if(variant.Locks!=null)
                                foreach(var pair in variant.Locks) locks[pair.Key]=new HashSet<int>(pair.Value);
                            bool changed=false;
                            foreach(var pair in failed.SuspectVertices) {
                                HashSet<int> set;
                                if(!locks.TryGetValue(pair.Key,out set))locks[pair.Key]=set=new HashSet<int>();
                                foreach(int index in pair.Value) if(set.Add(index))changed=true;
                            }
                            var pinned=changed?Simplifier.Reduce(source,exact,variant.Level,new HashSet<Mesh>(),locks):null;
                            if (pinned!=null) {
                                int n=Count(pinned);
                                if(n<exactCount)
                                    queue.Add(new Variant{Name=new LocalizedText("Profile.RegionRepair",Simplifier.ProfileLabel(variant.Level),variant.Repairs+1),
                                        Level=variant.Level,N=n,Faces=pinned,Hybrid=true,Repairs=variant.Repairs+1,Locks=locks});
                            }
                        }
                        if (!variant.Hybrid && failed.SuspectMeshes!=null && failed.SuspectMeshes.Count>0) {
                            var repaired=new Dictionary<Mesh,List<Tri>>(variant.Faces);
                            var lighter=levels[Simplifier.MilderLevel(variant.Level)];
                            foreach (var m in failed.SuspectMeshes)
                                if (repaired.ContainsKey(m) && lighter[m].Count>repaired[m].Count) repaired[m]=lighter[m];
                            int n=Count(repaired);
                            if (n>variant.N && n<exactCount && n!=Count(lighter))
                                queue.Add(new Variant { Name=new LocalizedText("Profile.LocalRepair",variant.Name),Level=variant.Level,N=n,Faces=repaired,Hybrid=true });
                        }
                    }
                    var search=new SurfaceSearch(source,quick,audit,detail,gameFile,gameName+".mdl",stop,progress,trials,bestFaces,finalView,selected,gameDefault);
                    search.Run(exact,exactView,exactDetail,DefaultCount(exactDisk),levels,options.Thorough);
                    finalDetail=search.Detail;qualityBudget=search.Budget;
                    selected=search.Selected;gameCount=search.Count;gameDefault=search.DefaultCount;finalView=search.Visual;visibleFaces=search.VisibleFaces;
                    if (gameCount==exactCount) {
                        if (File.Exists(gameFile)) File.Delete(gameFile);
                        source.Save(gameFile,exact,true,gameName+".mdl");
                        var copy=Studio.Load(gameFile); source.VerifyMetadata(copy);
                        finalView=audit.CompareSaved(copy,stop,(i,n)=>progress(80,new LocalizedText("Progress.CheckGameCopy")));
                        finalDetail=detail.CompareSaved(copy,stop,null);
                        if (!finalView.ExactMatch || !finalDetail.ExactMatch) throw LocalizedText.InvalidData("Core.FinalMismatch");
                    }
                }
                VisualResult reservedExact=null,reservedGame=null;
                if(options.Thorough) {
                    progress(91,new LocalizedText("Progress.CheckHeldOut"));
                    // The game geometry has already been selected. This diagnosis
                    // may flag an approximation, but never feeds its pixels into the search.
                    var reserved=VisualAudit.Reserved(source);
                    reservedExact=reserved.CompareSaved(exactDisk,stop,null);
                    if(!reservedExact.ExactMatch) {
                        var unchanged=Meshes(source).ToDictionary(m=>m,m=>m.Faces);
                        File.Delete(exFile);source.Save(exFile,unchanged,true,exName+".mdl");
                        exactDisk=Studio.Load(exFile);source.VerifyMetadata(exactDisk);
                        exactView=audit.CompareSaved(exactDisk,stop,null);exactDetail=detail.CompareSaved(exactDisk,stop,null);
                        reservedExact=reserved.CompareSaved(exactDisk,stop,null);
                        if(!exactView.ExactMatch || !exactDetail.ExactMatch || !reservedExact.ExactMatch)
                            throw LocalizedText.InvalidData("Core.OriginalHeldOutMismatch");
                        exactFallback=true;exactCount=Count(exactDisk);
                        if(selected=="exato") {
                            if(gameFile!=null) {
                                File.Delete(gameFile);source.Save(gameFile,unchanged,true,gameName+".mdl");
                                var copy=Studio.Load(gameFile);source.VerifyMetadata(copy);
                                finalView=audit.CompareSaved(copy,stop,null);finalDetail=detail.CompareSaved(copy,stop,null);
                                if(!finalView.ExactMatch || !finalDetail.ExactMatch)
                                    throw LocalizedText.InvalidData("Core.GameGeometryMismatch");
                            } else { finalView=exactView;finalDetail=exactDetail; }
                            gameCount=exactCount;gameDefault=DefaultCount(exactDisk);
                        }
                    }
                    if(gameFile!=null) {
                        reservedGame=reserved.CompareSaved(Studio.Load(gameFile),stop,null);
                        if(selected=="exato" && !reservedGame.ExactMatch)
                            throw LocalizedText.InvalidData("Core.GameHeldOutMismatch");
                        reservedGame.SavePreview(Path.Combine(staging,"auditoria-reservada.png"));
                    }
                }
                progress(94,new LocalizedText("Progress.Save"));
                finalView.SavePreview(Path.Combine(staging,"diferencas.png"));
                finalView.SavePair(Path.Combine(staging,"antes.png"),Path.Combine(staging,"depois.png"));
                finalView.SaveBodyPair(Path.Combine(staging,"corpo-antes.png"),Path.Combine(staging,"corpo-depois.png"));
                finalView.SaveBodyPreview(Path.Combine(staging,"corpo-diferencas.png"));
                string exactRelative=exName+"/"+exName+".mdl";
                string gameRelative=gameFile==null ? null : gameName+"/"+gameName+".mdl";
                var report=new { format_version=1, application_version=Version, input=Path.GetFileName(path),
                    input_sha256=originalHash, companions=companions, scan=options.Thorough?"completo":"rápido",
                    triangles_original=original, default_body_original=DefaultCount(source),
                    bones=source.Bones.Count,sequences=source.Sequences.Count,bodyparts=source.Parts.Count,
                    skins=source.Skins.Length,textures=source.Textures.Count,
                    exact=new { file=exactRelative,sha256=HashFile(exFile),triangles=exactCount,
                        default_body_triangles=DefaultCount(exactDisk),retained_original=exactFallback,visual=Metrics(exactView),detail=Metrics(exactDetail),
                        reserved=reservedExact==null?null:Metrics(reservedExact) },
                    game=gameFile==null ? null : (object)new { file=gameRelative,sha256=HashFile(gameFile),triangles=gameCount,
                        default_body_triangles=gameDefault,profile=selected,visual=Metrics(finalView),detail=Metrics(finalDetail),refinement_budget=qualityBudget,
                        reserved=reservedGame==null?null:Metrics(reservedGame) },
                    sampled_visible_faces=options.ExactOnly?(int?)null:visibleFaces,
                    body_view=finalView.BodyView,candidates=trials,seconds=Math.Round(watch.Elapsed.TotalSeconds,3) };
                File.WriteAllText(DiskPath.Full(Path.Combine(staging,"relatorio.json")),Json(report),new UTF8Encoding(false));
                if(HashFile(path)!=originalHash || source.CompanionPaths.Where((file,i)=>HashFile(file)!=companions[i].sha256).Any()) {
                    var message = new LocalizedText("Core.InputChanged");
                    throw message.Attach(new IOException(message.ToString()));
                }
                check();
                string dest=DiskPath.Display(Publish(staging,parent,stem));
                published=true;
                var diagnostic=reservedGame??reservedExact;
                return new RunResult { Input=DiskPath.Display(path),Directory=dest,ExactFile=Path.Combine(dest,exactRelative),
                    GameFile=gameRelative==null ? null : Path.Combine(dest,gameRelative), BodyView=finalView.BodyView,OriginalTriangles=original,
                    ExactTriangles=exactCount,GameTriangles=gameCount,WorstView=finalView.WorstView,Views=finalView.Views+finalDetail.Views,
                    WorstViewLabel=finalView.WorstViewLabel,BodyViewLabel=finalView.BodyViewLabel,
                    Silhouette=finalView.Silhouette,ColorError=finalView.MeanColor,Seconds=watch.Elapsed.TotalSeconds,
                    UsedExact=selected=="exato",ReservedPassed=diagnostic==null?(bool?)null:diagnostic.Passed,
                    ReservedViews=diagnostic==null?0:diagnostic.Views,ReservedFailedViews=diagnostic==null?0:diagnostic.FailedViews };
            }
            finally { if (!published && Directory.Exists(staging)) Directory.Delete(DiskPath.Tree(staging),true); }
        }
    }
}
