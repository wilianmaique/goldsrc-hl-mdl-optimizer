using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;
using MdlOptimizer;

// Replays the application's post-selection audit with per-view reporting.
internal static class HoldoutTests
{
    static readonly CultureInfo Inv=CultureInfo.InvariantCulture;

    static string HashFile(string file)
    {
        using(var input=File.OpenRead(DiskPath.Full(file)))using(var sha=SHA256.Create())
            return BitConverter.ToString(sha.ComputeHash(input)).Replace("-","").ToLowerInvariant();
    }

    static int Main(string[] args)
    {
        Console.OutputEncoding=new UTF8Encoding(false);
        if(args.Length<3){Console.Error.WriteLine("Informe original.mdl, pasta de relatório vazia e um ou mais candidatos .mdl.");return 2;}
        try {
            string dir=DiskPath.Full(args[1]);
            if(Directory.Exists(dir) && Directory.EnumerateFileSystemEntries(dir).Any())
                throw new IOException("A pasta de relatório precisa estar vazia.");
            Directory.CreateDirectory(dir);
            var original=Studio.Load(args[0]);
            var audit=VisualAudit.Reserved(original);
            var control=audit.CompareSaved(original,null,null);
            if(!control.Passed || !control.ExactMatch)throw new Exception("O controle original contra original divergiu.");
            var rows=new List<object>();
            var json=new JavaScriptSerializer {MaxJsonLength=Int32.MaxValue};
            bool passed=true;
            for(int i=2;i<args.Length;i++) {
                string path=args[i]; var other=Studio.Load(path);
                original.VerifyMetadata(other);
                // Workers fill independent slots; CompareSaved joins them before the write.
                var views=new object[audit.ViewCount];
                audit.OnViewMeasured=delegate(int index,AuditView v,ViewError m) {
                    views[index]=new {index=index,view=v.Name,sequence=v.Sequence,frame=v.Frame,blend=v.Blend,
                        skin=v.Skin,bodygroup_models=v.Models,close_up=v.Focus!=-1,azimuth=v.Azimuth,elevation=v.Elevation,
                        resolution=v.Resolution,passed=m.Pass,exact_pixels=m.Changed==0,pixel_union=m.Union,silhouette_iou=m.IoU,
                        mean_error_rgb_255=m.Mean,material_change_ratio=(double)m.Material/Math.Max(1,m.Union),
                        worst_ratio_to_limit=m.Severity};
                };
                var r=audit.CompareSaved(other,null,null);
                passed&=r.Passed;
                string prefix=(i-1).ToString("00",Inv)+"-"+Path.GetFileNameWithoutExtension(path);
                r.SavePreview(Path.Combine(dir,prefix+".png"));
                File.WriteAllText(DiskPath.Full(Path.Combine(dir,prefix+"-views.json")),json.Serialize(views),new UTF8Encoding(false));
                rows.Add(new {file=Path.GetFileName(path),sha256=HashFile(path),triangles=other.TriangleCount,
                    passed=r.Passed,exact_pixels=r.ExactMatch,views=r.Views,failed_views=r.FailedViews,global_silhouette_iou=r.Silhouette,
                    worst_silhouette_iou=r.WorstSilhouette,mean_error_rgb_255=r.MeanColor,
                    material_change_ratio=r.MaterialChange,worst_mean_error_rgb_255=r.WorstColor,worst_view=r.WorstView,
                    per_view=prefix+"-views.json",preview=prefix+".png"});
                Console.WriteLine(String.Format(Inv,"{0}: {1} tri; {2}/{3} vistas reprovadas; IoU {4:P3}; MAE {5:F3}; {6}",
                    Path.GetFileName(path),other.TriangleCount,r.FailedViews,r.Views,r.Silhouette,r.MeanColor,r.Passed?"PASS":"FAIL"));
            }
            var report=new {plan="independent-camera-fold-v1",original=Path.GetFileName(args[0]),sha256=HashFile(args[0]),
                control_exact=true,control_views=control.Views,candidates=rows};
            File.WriteAllText(DiskPath.Full(Path.Combine(dir,"holdout.json")),json.Serialize(report),new UTF8Encoding(false));
            return passed?0:3;
        } catch(Exception ex){Console.Error.WriteLine(ex.Message);return 1;}
    }
}
