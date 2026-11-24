using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

[assembly: InternalsVisibleTo("VisualRasterTests")]

namespace MdlOptimizer
{
    public sealed class VisualResult
    {
        public bool Passed, ExactMatch;
        public int Views,FailedViews;
        internal int WorstIndex;
        public string WorstView, BodyView;
        internal LocalizedText WorstViewLabel, BodyViewLabel;
        public double MeanColor, Silhouette, WorstSilhouette, MaterialChange, WorstColor;
        public HashSet<Mesh> SuspectMeshes = new HashSet<Mesh>();
        public Dictionary<Mesh,HashSet<int>> SuspectVertices = new Dictionary<Mesh,HashSet<int>>();
        internal ViewImage Before,After,BodyBefore,BodyAfter;
        internal int[] PreviewMaterials,BodyPreviewMaterials;
        const int Outline=0xda293e, Depth=0x2166ac, Material=0xa13ca8, MaterialAndDepth=0x00845b, ColorDifference=0xff7e10;
        const int PreviewGap=8, PreviewHeader=32, PreviewLegend=49;

        static Bitmap BitmapFrom(int[] colors,int w,int h)
        {
            var bmp=new Bitmap(w,h,PixelFormat.Format32bppRgb);
            BitmapData data=bmp.LockBits(new Rectangle(0,0,w,h),ImageLockMode.WriteOnly,PixelFormat.Format32bppRgb);
            try {
                for(int y=0;y<h;y++) Marshal.Copy(colors,y*w,IntPtr.Add(data.Scan0,y*data.Stride),w);
            } finally { bmp.UnlockBits(data); }
            return bmp;
        }

        public void SavePair(string beforePath,string afterPath)
        {
            WritePair(Before,After,beforePath,afterPath);
        }
        public void SaveBodyPair(string beforePath,string afterPath)
        {
            WritePair(BodyBefore,BodyAfter,beforePath,afterPath);
        }
        static void WritePair(ViewImage before,ViewImage after,string beforePath,string afterPath)
        {
            using(Bitmap a=BitmapFrom(before.Color,before.Width,before.Height)) WritePng(a,beforePath);
            using(Bitmap b=BitmapFrom(after.Color,after.Width,after.Height)) WritePng(b,afterPath);
        }
        static void WritePng(Bitmap image,string path)
        {
            using(var stream=new FileStream(DiskPath.Full(path),FileMode.CreateNew,FileAccess.Write,FileShare.None))
                image.Save(stream,ImageFormat.Png);
        }

        public void SavePreview(string outputPath)
        {
            WritePreview(Before,After,PreviewMaterials,outputPath);
        }
        public void SaveBodyPreview(string outputPath)
        {
            WritePreview(BodyBefore,BodyAfter,BodyPreviewMaterials,outputPath);
        }
        static void WritePreview(ViewImage before,ViewImage after,int[] materials,string outputPath)
        {
            int w=before.Width,h=before.Height;
            var diff=new int[before.Color.Length];
            double depthLimit=Math.Max(3.0,.055*Math.Max(w,h));
            for(int i=0;i<diff.Length;i++) {
                int a=before.Color[i], b=after.Color[i];
                int firstOwner=before.Owner[i], secondOwner=after.Owner[i];
                if((firstOwner==0)!=(secondOwner==0)) {diff[i]=Outline;continue;}
                if(firstOwner==0) {diff[i]=ViewImage.Background;continue;}
                bool newMaterial=materials[firstOwner]!=materials[secondOwner];
                // The per-view detector compares projected depth only on occupied pixels with finite depth.
                float za=before.Depth[i], zb=after.Depth[i];
                bool newLayer=!float.IsInfinity(za) && !float.IsInfinity(zb) && Math.Abs(za-zb)*before.Camera.Scale>depthLimit;
                if(newMaterial || newLayer) {
                    diff[i]=newMaterial?(newLayer?MaterialAndDepth:Material):Depth;
                    continue;
                }
                int d=Math.Max(Math.Abs((a>>16)-(b>>16)),Math.Max(Math.Abs(((a>>8)&255)-((b>>8)&255)),Math.Abs((a&255)-(b&255))));
                int gray=116+((a>>16)+((a>>8)&255)+(a&255))/10;
                if(d==0) diff[i]=gray*0x010101;
                else { int alpha=Math.Min(255,8*d); diff[i]=Mix(gray,255,alpha)<<16|Mix(gray,126,alpha)<<8|Mix(gray,16,alpha); }
            }
            int panel=Math.Max(160,w);
            using(Bitmap first=BitmapFrom(before.Color,w,h))
            using(Bitmap next=BitmapFrom(after.Color,w,h))
            using(Bitmap heat=BitmapFrom(diff,w,h))
            using(Bitmap sheet=new Bitmap(panel*3+PreviewGap*4,PreviewHeader+h+PreviewGap+PreviewLegend)) {
                using(Graphics g=Graphics.FromImage(sheet)) {
                    g.Clear(Color.FromArgb(225,230,235));
                    Bitmap[] images={first,next,heat};
                    for(int p=0;p<3;p++) {
                        int x=PreviewGap+p*(panel+PreviewGap);
                        g.DrawImageUnscaled(images[p],x+(panel-w)/2,PreviewHeader);
                    }
                }
                LocalizePreview(sheet,h);
                WritePng(sheet,outputPath);
            }
        }

        // Both exported sheets and the in-memory GUI preview use the same
        // label areas. Relabelling an open preview leaves its files untouched.
        public static void LocalizePreview(Bitmap sheet,int imageHeight)
        {
            if(sheet==null || imageHeight<=0 || sheet.Height!=PreviewHeader+imageHeight+PreviewGap+PreviewLegend ||
                sheet.Width<160*3+PreviewGap*4 || (sheet.Width-PreviewGap*4)%3!=0) return;
            int panel=(sheet.Width-PreviewGap*4)/3, legendY=PreviewHeader+imageHeight+PreviewGap;
            string language=L.Language;
            string[] headings={"Legend.Original","Legend.Optimized","Legend.Differences"};
            string[] legend={"Legend.Outline","Legend.Depth","Legend.Material","Legend.Rgb","Legend.MaterialDepth"};
            int[] colors={Outline,Depth,Material,ColorDifference,MaterialAndDepth};
            using(Graphics g=Graphics.FromImage(sheet))
            using(Font font=new Font("Segoe UI",10f,FontStyle.Regular))
            using(var background=new SolidBrush(Color.FromArgb(225,230,235))) {
                g.FillRectangle(background,0,0,sheet.Width,PreviewHeader);
                g.FillRectangle(background,0,legendY,sheet.Width,PreviewLegend);
                for(int i=0;i<headings.Length;i++)
                    g.DrawString(L.Format(language,headings[i]),font,Brushes.Black,PreviewGap+i*(panel+PreviewGap),7);
                for(int i=0;i<legend.Length;i++) {
                    int x=PreviewGap+(i%3)*(panel+PreviewGap), y=legendY+(i/3)*21;
                    using(var brush=new SolidBrush(Color.FromArgb(colors[i]|unchecked((int)0xff000000))))
                        g.FillRectangle(brush,x,y+4,11,11);
                    g.DrawString(L.Format(language,legend[i]),font,Brushes.Black,x+15,y);
                }
            }
        }
        static int Mix(int a,int b,int alpha) { return (a*(255-alpha)+b*alpha)/255; }
    }

    internal sealed class ViewError
    {
        internal int Union,Intersection,Material,ColorSum,Changed;
        internal double IoU,Mean,Severity;
        internal bool Pass;
        internal long[] Owners;
    }

    public sealed class VisualAudit
    {
        readonly AuditGeometry reference;
        readonly List<AuditView> plan=new List<AuditView>();
        readonly List<bool[]> focusSets=new List<bool[]>();
        readonly List<string> focusNames=new List<string>();
        readonly bool thorough;
        public int ViewCount { get { return plan.Count; } }
        internal Action<int,AuditView,ViewError> OnViewMeasured { get; set; }
        static readonly CultureInfo Invariant=CultureInfo.InvariantCulture;

        public VisualAudit(Studio original,bool thorough) : this(original,thorough,null) {}

        // A separate camera configuration can reuse the identical detector and tolerances.
        internal VisualAudit(Studio original,bool thorough,Action<AuditView,int> configureCamera,bool detailSweep=false)
        {
            if(original==null)throw new ArgumentNullException("original");
            this.thorough=thorough;
            reference=new AuditGeometry(original);
            BuildPlan(original);
            if(detailSweep)BuildDetailSweep(original);
            if(configureCamera!=null)
                for(int i=0;i<plan.Count;i++)configureCamera(plan[i],i);
            // Budget using uncropped square view dimensions (actual rectangles are smaller).
            long room=96L*1024*1024;
            foreach(AuditView v in plan) {
                long bytes=(long)v.Resolution*v.Resolution*12;
                if(bytes<=room) { v.Cache=true; room-=bytes; }
            }
        }

        public static VisualAudit DetailSweep(Studio original,bool thorough)
        {
            return new VisualAudit(original,thorough,null,true);
        }

        // independent-camera-fold-v1. Run only after candidate selection.
        public static VisualAudit Reserved(Studio original)
        {
            return new VisualAudit(original,true,(v,i)=>RotateCamera(original,v,i));
        }

        static void RotateCamera(Studio s,AuditView v,int index)
        {
            // Offset both angles to sample different directions even for one-frame clips.
            v.Azimuth=(v.Azimuth+67.1875+(index%11)*5.3125)%360;
            v.Elevation=Math.Max(-80,Math.Min(80,v.Elevation*.76+((index*37)%43)-21+.375));
            if(v.Sequence>=0) {
                Sequence q=s.Sequences[v.Sequence];
                v.Frame=(v.Frame+Math.Max(1,q.Frames*7/17))%q.Frames;
                v.Blend=(v.Blend+(q.Blends+1)/2)%q.Blends;
            }
            v.Skin=(v.Skin+1)%s.Skins.Length;
            SetViewName(s,v,true);
        }

        void BuildDetailSweep(Studio s)
        {
            // Keep the historically comparable camera set intact. Sample additional
            // anatomy and full-body views across the rig's actual sequence range.
            var alternatives=plan.FindAll(v=>v.Category.Key=="View.Bodygroup");
            plan.Clear();
            int regions=Math.Max(3,focusSets.Count), directions=thorough?8:3;
            for(int region=-1;region<regions;region++) for(int i=0;i<directions;i++) {
                int ordinal=(region+1)*directions+i;
                int seq=s.Sequences.Count==0?-1:(int)((long)(2*i+1)*s.Sequences.Count/(2*directions)+region+1)%s.Sequences.Count;
                int focus=region<focusSets.Count?region:-2-(region%3);
                int blend=seq<0?0:ordinal%s.Sequences[seq].Blends;
                int[] body=new int[s.Parts.Count];
                for(int p=0;p<body.Length;p++)body[p]=i==0?0:(i+p)%s.Parts[p].Models.Count;
                double az=(ordinal*137.507764+17)%360;
                AddView(s,seq,(.5+((i*5+region+1)%directions))/directions,blend,ordinal%s.Skins.Length,
                    body,az,-32+64.0*(i+.5)/directions,thorough?272:176,focus,
                    region<0 ? new LocalizedText("View.ExtraBody") : region<focusSets.Count ?
                    new LocalizedText("View.ExtraDetail",focusNames[region]) : new LocalizedText("View.ExtraSpatial"),region<0);
            }
            plan.AddRange(alternatives);
        }

        static string Canon(string s) { return s.ToLowerInvariant().Replace("_"," ").Replace("-"," "); }
        static int SelectSequence(Studio s,int fallback,params string[] names)
        {
            foreach(string n in names) for(int j=0;j<s.Sequences.Count;j++)
                if(Canon(s.Sequences[j].Name).Contains(n)) return j;
            return s.Sequences.Count==0 ? -1 : Math.Min(s.Sequences.Count-1,fallback);
        }

        void AddFocus(Studio s,string[] aliases,int limit)
        {
            var used=new HashSet<int>();
            foreach(string alias in aliases) for(int i=0;i<s.Bones.Count && used.Count<limit;i++) {
                string name=Canon(s.Bones[i].Name);
                if(!name.Contains(alias)||used.Contains(i))continue;
                bool nested=false;
                for(int p=s.Bones[i].Parent;p>=0;p=s.Bones[p].Parent) if(used.Contains(p)) { nested=true;break; }
                if(nested)continue;
                var bones=new bool[s.Bones.Count];bones[i]=true;
                for(int child=0;child<s.Bones.Count;child++) {
                    int parent=s.Bones[child].Parent;
                    if(parent>=0 && bones[parent])bones[child]=true;
                }
                // A named helper can contain no geometry at all.
                int bound=0;
                foreach(Part part in s.Parts) foreach(Sub sub in part.Models)
                    foreach(byte b in sub.VertexBones) if(bones[b])bound++;
                if(bound<3)continue;
                used.Add(i);focusSets.Add(bones);focusNames.Add(s.Bones[i].Name);
            }
        }

        static void SetViewName(Studio s,AuditView view,bool reserved)
        {
            string sequence=view.Sequence<0?"bind":s.Sequences[view.Sequence].Name;
            // Keep the historical machine-readable camera identifier and its
            // invariant coordinates separate from the translated caption.
            view.Name=String.Format(Invariant,reserved ? "{0} · {1} · f{2} b{3} · {4:F2}°/{5:F2}°" :
                "{0} · {1} · f{2} b{3} · {4:0}°/{5:0}°",view.Category.StableText,sequence,
                view.Frame,view.Blend,view.Azimuth,view.Elevation);
            object caption=view.Sequence<0 ? (object)new LocalizedText("View.BindPose") : sequence;
            view.DisplayName=new LocalizedText(reserved ? "View.ReservedCamera" : "View.Camera",
                view.Category,caption,view.Frame,view.Blend,view.Azimuth,view.Elevation);
            if(reserved || s.Skins.Length>1) {
                view.Name+=" · skin "+view.Skin.ToString(Invariant);
                view.DisplayName=new LocalizedText("View.Skin",view.DisplayName,view.Skin);
            }
        }

        void AddView(Studio s,int seq,double fraction,int blend,int skin,int[] models,double az,double el,
                     int res,int focus,LocalizedText category,bool mainBody=false)
        {
            int frame=seq<0?0:(int)Math.Round((s.Sequences[seq].Frames-1)*Math.Max(0,Math.Min(1,fraction)));
            blend=seq<0?0:Math.Min(s.Sequences[seq].Blends-1,blend);
            var view=new AuditView {Sequence=seq,Frame=frame,Blend=blend,Skin=skin,Models=models,Azimuth=az,
                Elevation=el,Resolution=res,Focus=focus,Category=category,MainBody=mainBody};
            SetViewName(s,view,false);
            plan.Add(view);
        }

        void BuildPlan(Studio s)
        {
            AddFocus(s,new[]{"head","skull","cabeça"},1);
            AddFocus(s,new[]{"hand","wrist","mão"},2);
            AddFocus(s,new[]{"foot","ankle","pé"},2);
            int[] allDefault=new int[s.Parts.Count];
            int count=s.Sequences.Count;
            var sequences=new List<int>();
            int[] selected={SelectSequence(s,0,"idle","stand"),SelectSequence(s,count/3,"crouch","duck"),
                SelectSequence(s,count/2,"run","walk","move"),SelectSequence(s,2*count/3,"attack","shoot","aim"),
                SelectSequence(s,3*count/4,"jump","leap","fly"),SelectSequence(s,Math.Max(0,count-1),"death","die")};
            foreach(int q in selected) if(!sequences.Contains(q))sequences.Add(q);
            int mainSize=thorough?248:164, closeSize=thorough?304:204;
            int bodyAngles=thorough?4:2;
            for(int pose=0;pose<sequences.Count;pose++) for(int angle=0;angle<bodyAngles;angle++) {
                int seq=sequences[pose];
                int blend=seq<0?0:(angle*(s.Sequences[seq].Blends-1)/Math.Max(1,bodyAngles-1));
                double[] elevations={5,-17,22,-4};
                AddView(s,seq,pose==0?0:(.18+.16*angle),blend,(pose+angle)%s.Skins.Length,allDefault,
                        (315+angle*90+pose*43)%360,elevations[(angle+pose)%4],mainSize,-1,new LocalizedText("View.Body"),true);
            }
            for(int region=0;region<focusSets.Count;region++) {
                int n=thorough?4:2;
                for(int v=0;v<n;v++) {
                    int q=sequences[v%sequences.Count];
                    AddView(s,q,.24*v,q<0?0:(v%2)*(s.Sequences[q].Blends-1),(region+v)%s.Skins.Length,allDefault,
                        (315+v*91+region*11)%360,new[]{4,-14,17,8}[v],closeSize,region,new LocalizedText("View.Detail",focusNames[region]));
                }
            }
            // Props and non-humanoid rigs often use anonymous bone names.
            // Still inspect local surfaces at higher magnification in that case.
            if(focusSets.Count<2)for(int area=0;area<3;area++) {
                int n=thorough?2:1;
                for(int v=0;v<n;v++)AddView(s,sequences[(area+v)%sequences.Count],.38,0,
                    (area+v)%s.Skins.Length,allDefault,35+v*127+area*27,area*9-8,closeSize,-2-area,
                    new LocalizedText("View.Spatial",area+1));
            }
            // Exercise every individual alternate body part and skin family without
            // multiplying all combinations together. Full sequence sweeps vary combinations too.
            for(int p=0;p<s.Parts.Count;p++) for(int a=1;a<s.Parts[p].Models.Count;a++) {
                int[] choice=(int[])allDefault.Clone();choice[p]=a;
                AddView(s,sequences[0],.23,0,a%s.Skins.Length,choice,315,5,mainSize,-1,
                    new LocalizedText("View.Bodygroup",s.Parts[p].Name,a));
            }
            for(int skin=1;skin<s.Skins.Length;skin++) AddView(s,sequences[0],.36,0,skin,allDefault,35,2,mainSize,-1,new LocalizedText("View.SkinFamily"));
            if(thorough)for(int q=0;q<count;q++) for(int v=0;v<2;v++) {
                int[] choice=new int[s.Parts.Count];
                for(int p=0;p<choice.Length;p++) choice[p]=(q+p+v)%s.Parts[p].Models.Count;
                Sequence seq=s.Sequences[q];
                AddView(s,q,((q*29+v*53+11)%101)/100.0,(q+v*(seq.Blends-1))%seq.Blends,
                    (q+v)%s.Skins.Length,choice,(q*137+31+v*119)%360,((q*13+v*27)%67)-33,132,-1,new LocalizedText("View.Sequence"));
            }
        }

        public VisualResult Compare(Dictionary<Mesh,List<Tri>> faces,Func<bool> cancel,Action<int,int> progress)
        {
            return Run(reference,faces,cancel,progress);
        }

        static Framing CameraFrame(AuditView view,Box2 full,Box2 region)
        {
            if(view.Focus< -1 && !full.Empty) {
                float w=full.X1-full.X0,h=full.Y1-full.Y0;
                float y=full.Y0+(0.2f+(-2-view.Focus)*.30f)*h;
                region=new Box2{X0=full.X0+.20f*w,X1=full.X1-.20f*w,Y0=y-h*.16f,Y1=y+h*.16f};
            }
            return AuditGeometry.Frame(view.Resolution,full,region,view.Focus!=-1);
        }

        public VisualResult CompareSaved(Studio saved,Func<bool> cancel,Action<int,int> progress)
        {
            var geom=new AuditGeometry(saved);
            if(geom.Meshes.Count!=reference.Meshes.Count || saved.Parts.Count!=reference.Studio.Parts.Count ||
               saved.Bones.Count!=reference.Studio.Bones.Count || saved.Sequences.Count!=reference.Studio.Sequences.Count ||
               saved.Skins.Length!=reference.Studio.Skins.Length)
                throw LocalizedText.InvalidData("Core.StructureMismatch");
            return Run(geom,null,cancel,progress);
        }

        VisualResult Run(AuditGeometry candidate,Dictionary<Mesh,List<Tri>> edit,Func<bool> cancel,Action<int,int> progress)
        {
            var output=new VisualResult {Passed=true,ExactMatch=true,WorstSilhouette=1,Views=plan.Count};
            int complete=0,worst=-1;double worstSeverity=-1;
            int body=-1,bodyPriority=-1;double bodySeverity=-1;
            long union=0,intersection=0,color=0,material=0;
            long[] blame=new long[reference.Meshes.Count+1];
            object gate=new object();
            using(var stop=new CancellationTokenSource()) {
                var options=new ParallelOptions { MaxDegreeOfParallelism=Math.Max(1,Math.Min(4,Environment.ProcessorCount)),
                    CancellationToken=stop.Token };
                Parallel.For(0,plan.Count,options,delegate(int index) {
                    if(cancel!=null && cancel()) { stop.Cancel();stop.Token.ThrowIfCancellationRequested(); }
                    AuditView view=plan[index];
                    bool[] focus=view.Focus<0?null:focusSets[view.Focus];
                    Box2 full,region;
                    ViewImage original=view.Reference;
                    Framing frame=original==null?null:original.Camera;
                    List<ScreenSub> projected=null;
                    bool sameGeometry=Object.ReferenceEquals(reference,candidate);
                    if(original==null || sameGeometry) {
                        projected=reference.Project(view,focus,out full,out region,frame==null);
                        if(frame==null)frame=CameraFrame(view,full,region);
                        AuditGeometry.ApplyFrame(projected,frame);
                        if(original==null) {
                            original=reference.Draw(view,projected,frame,null);
                            if(view.Cache)view.Reference=original;
                        }
                    }
                    if(!sameGeometry) {
                        projected=candidate.Project(view,focus,out full,out region,false);
                        AuditGeometry.ApplyFrame(projected,frame);
                    }
                    ViewImage variant=candidate.Draw(view,projected,frame,edit);
                    var metrics=Measure(original,variant,reference.SkinByOwner[view.Skin],view.Focus!=-1,frame.Scale);
                    if(OnViewMeasured!=null)OnViewMeasured(index,view,metrics);
                    lock(gate) {
                        output.Passed &= metrics.Pass; output.ExactMatch &= metrics.Changed==0;
                        if(!metrics.Pass)output.FailedViews++;
                        union+=metrics.Union; intersection+=metrics.Intersection; color+=metrics.ColorSum; material+=metrics.Material;
                        output.WorstColor=Math.Max(output.WorstColor,metrics.Mean);
                        output.WorstSilhouette=Math.Min(output.WorstSilhouette,metrics.IoU);
                        if(!metrics.Pass)for(int j=1;j<blame.Length;j++)blame[j]+=metrics.Owners[j];
                        if(metrics.Union>0 && (metrics.Severity>worstSeverity || (metrics.Severity==worstSeverity && index<worst))) {
                            worst=index;worstSeverity=metrics.Severity;output.Before=original;output.After=variant;output.WorstView=view.Name;
                            output.WorstViewLabel=view.DisplayName;output.PreviewMaterials=reference.SkinByOwner[view.Skin];
                        }
                        if(view.Focus==-1) {
                            // When default bodygroups are blank, use a nonempty alternate/sweep.
                            int priority=(metrics.Union>0?2:0)+(view.MainBody?1:0);
                            if(priority>bodyPriority || (priority==bodyPriority && (metrics.Severity>bodySeverity ||
                                (metrics.Severity==bodySeverity && index<body)))) {
                                body=index;bodyPriority=priority;bodySeverity=metrics.Severity;
                                output.BodyBefore=original;output.BodyAfter=variant;output.BodyView=view.Name;
                                output.BodyViewLabel=view.DisplayName;output.BodyPreviewMaterials=reference.SkinByOwner[view.Skin];
                            }
                        }
                        complete++;
                        if(progress!=null)progress(complete,plan.Count);
                    }
                });
            }
            if(union==0)throw LocalizedText.InvalidData("Core.NoSurface");
            output.MeanColor=color/(3.0*union);
            output.Silhouette=(double)intersection/union;
            output.MaterialChange=(double)material/union;
            output.WorstIndex=worst;
            output.Passed &= output.MeanColor<=3.3 && output.Silhouette>=.989 && output.MaterialChange<=.014;
            long max=0;
            for(int i=1;i<blame.Length;i++)max=Math.Max(max,blame[i]);
            if(max>0)for(int i=1;i<blame.Length;i++)
                if(blame[i]>0 && blame[i]>=max*.22)output.SuspectMeshes.Add(reference.Meshes[i-1]);
            if(!output.Passed) {
                if(cancel!=null && cancel())throw new OperationCanceledException();
                FindProblemVertices(output,plan[worst],candidate,edit);
            }
            return output;
        }

        internal void LocateDifferences(VisualResult result,Dictionary<Mesh,List<Tri>> faces)
        {
            FindProblemVertices(result,plan[result.WorstIndex],reference,faces);
        }

        void FindProblemVertices(VisualResult result,AuditView view,AuditGeometry candidate,Dictionary<Mesh,List<Tri>> edits)
        {
            // Render face IDs only for the worst view so the large reference cache
            // never contains topology maps. Score the four worst screen regions.
            Box2 full,region;
            bool[] focus=view.Focus<0?null:focusSets[view.Focus];
            var projected=reference.Project(view,focus,out full,out region);
            Framing frame=CameraFrame(view,full,region);
            AuditGeometry.ApplyFrame(projected,frame);
            ViewImage a=reference.Draw(view,projected,frame,null,true);
            bool sameIndex=Object.ReferenceEquals(candidate,reference);
            ViewImage b=sameIndex ? candidate.Draw(view,projected,frame,edits,true) : result.After;
            int[] mat=reference.SkinByOwner[view.Skin];
            int tile=Math.Max(10,Math.Max(a.Width,a.Height)/12),nx=(a.Width+tile-1)/tile;
            int[] scores=new int[nx*((a.Height+tile-1)/tile)];
            int[] weights=new int[a.Owner.Length];
            for(int y=0,i=0;y<a.Height;y++)for(int x=0;x<a.Width;x++,i++) {
                if((a.Owner[i]|b.Owner[i])==0)continue;
                int ca=a.Color[i],cb=b.Color[i];
                int delta=Math.Abs((ca>>16)-(cb>>16))+Math.Abs(((ca>>8)&255)-((cb>>8)&255))+Math.Abs((ca&255)-(cb&255));
                int weight=Math.Max(0,delta-30)+(mat[a.Owner[i]]!=mat[b.Owner[i]]?200:0);
                if(a.Owner[i]!=0 && b.Owner[i]!=0 && !float.IsInfinity(a.Depth[i]) && !float.IsInfinity(b.Depth[i]) &&
                    Math.Abs(a.Depth[i]-b.Depth[i])*frame.Scale>Math.Max(3,.055*Math.Max(a.Width,a.Height)))weight+=160;
                weights[i]=weight; scores[(y/tile)*nx+x/tile]+=weight;
            }
            int[] sorted=(int[])scores.Clone();Array.Sort(sorted);
            int cutoff=Math.Max(1,Math.Max(sorted[sorted.Length-1]/4,sorted[Math.Max(0,sorted.Length-4)]));
            var faces=new Dictionary<long,int>();
            for(int y=0,i=0;y<a.Height;y++)for(int x=0;x<a.Width;x++,i++) {
                if(weights[i]==0||scores[(y/tile)*nx+x/tile]<cutoff)continue;
                if(a.Owner[i]!=0)ScoreFace(faces,a.Owner[i],a.Face[i],false,weights[i]);
                if(sameIndex&&b.Owner[i]!=0)ScoreFace(faces,b.Owner[i],b.Face[i],true,weights[i]);
            }
            var ordered=new List<KeyValuePair<long,int>>(faces);
            ordered.Sort(delegate(KeyValuePair<long,int> first,KeyValuePair<long,int> second) {
                int c=second.Value.CompareTo(first.Value); return c!=0?c:first.Key.CompareTo(second.Key);
            });
            int limit=Math.Min(96,Math.Max(12,reference.Studio.TriangleCount/90));
            for(int i=0;i<ordered.Count&&i<limit;i++) {
                long key=ordered[i].Key; int owner=(int)(key>>33),face=(int)(key&0x7fffffffL);
                Mesh mesh=reference.Meshes[owner-1];
                List<Tri> source=mesh.Faces,edited;
                if((key&0x100000000L)!=0 && edits!=null && edits.TryGetValue(mesh,out edited))source=edited;
                Tri t=source[face];
                HashSet<int> vertices;
                if(!result.SuspectVertices.TryGetValue(mesh,out vertices)) {
                    vertices=new HashSet<int>(); result.SuspectVertices.Add(mesh,vertices);
                }
                vertices.Add(t.A.V);vertices.Add(t.B.V);vertices.Add(t.C.V);
            }
        }

        static void ScoreFace(Dictionary<long,int> scores,int owner,int face,bool edited,int weight)
        {
            long key=((long)owner<<33)|(edited?0x100000000L:0L)|(uint)face;
            int total;scores.TryGetValue(key,out total);scores[key]=total+weight;
        }

        static double ClampRatio(double n,double limit) { return n/limit; }

        internal static ViewError Measure(ViewImage a,ViewImage b,int[] materials,bool close,float pixelsPerUnit)
        {
            var result=new ViewError { Owners=new long[materials.Length] };
            int high=0,tile=Math.Max(9,Math.Min(28,Math.Max(a.Width,a.Height)/9)),nx=(a.Width+tile-1)/tile;
            int cells=nx*((a.Height+tile-1)/tile);
            var footprint=new int[cells];var structural=new int[cells];var strong=new int[cells];
            // Depth discontinuities can expose a distant layer of the very same
            // black material. Compare depth in screen-equivalent units, so the
            // threshold follows model scale and the close-up magnification.
            var layer=new int[cells]; int gaps=0;
            double depthLimit=Math.Max(3.0,.055*Math.Max(a.Width,a.Height));
            for(int y=0,i=0;y<a.Height;y++)for(int x=0;x<a.Width;x++,i++) {
                int oa=a.Owner[i], ob=b.Owner[i];
                if((oa|ob)==0)continue;
                result.Union++;if(oa!=0&&ob!=0)result.Intersection++;
                int cell=(y/tile)*nx+x/tile; footprint[cell]++;
                bool type=materials[oa]!=materials[ob]; if(type)result.Material++;
                int ca=a.Color[i],cb=b.Color[i];
                int dr=Math.Abs((ca>>16)-(cb>>16)),dg=Math.Abs(((ca>>8)&255)-((cb>>8)&255)),db=Math.Abs((ca&255)-(cb&255));
                int distance=dr+dg+db, largest=Math.Max(dr,Math.Max(dg,db));
                bool shift=oa!=0&&ob!=0&&!float.IsInfinity(a.Depth[i])&&!float.IsInfinity(b.Depth[i])&&a.Depth[i]!=b.Depth[i];
                bool gap=shift && Math.Abs(a.Depth[i]-b.Depth[i])*pixelsPerUnit>depthLimit;
                if(gap) { layer[cell]++;gaps++; }
                if(type||ca!=cb||shift)result.Changed++;
                result.ColorSum+=distance;
                if(largest>32)high++;
                if(type)structural[cell]++;
                if(largest>75)strong[cell]++;
                int weight=distance+(type?256:0)+(gap?160:0);
                result.Owners[oa]+=weight; if(oa!=ob)result.Owners[ob]+=weight;
            }
            double count=Math.Max(1,result.Union);
            result.IoU=result.Union==0?1:result.Intersection/count;
            result.Mean=result.ColorSum/(3*count);
            double local=0;
            for(int k=0;k<cells;k++) {
                // Avoid failing on a handful of pixels at the outermost tip of a limb.
                if(footprint[k]<tile*tile*.38)continue;
                double n=footprint[k];
                local=Math.Max(local,Math.Max(ClampRatio(structural[k]/n,close?.20:.27),ClampRatio(strong[k]/n,close?.22:.32)));
                local=Math.Max(local,ClampRatio(layer[k]/n,close?.25:.32));
            }
            result.Severity=Math.Max(local, Math.Max(ClampRatio(1-result.IoU,close?.022:.034),
                Math.Max(ClampRatio(result.Mean,close?5.3:6), Math.Max(ClampRatio(result.Material/count,close?.034:.044),
                ClampRatio(high/count,close?.080:.085)))));
            result.Severity=Math.Max(result.Severity,ClampRatio(gaps/count,close?.045:.065));
            result.Pass=result.Severity<=1;
            return result;
        }
    }
}
