using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using MdlOptimizer;

// Synthetic render targets isolate properties that are hard to prove with a
// screenshot of an entire character. This executable needs no GPU or game.
public static class VisualRasterTests
{
    static int assertions;

    static void Check(bool condition,string description)
    {
        if (!condition) throw new InvalidOperationException(description);
        assertions++;
    }

    static Texture Material(int rgb,int flags,int index)
    {
        var palette=new byte[768];
        palette[index*3]=(byte)(rgb>>16);
        palette[index*3+1]=(byte)(rgb>>8);
        palette[index*3+2]=(byte)rgb;
        return new Texture {Name="test",Width=1,Height=1,Flags=flags,Palette=palette,Pixels=new[]{(byte)index}};
    }

    static int Pixel(Texture[] layers,float[] depth,bool[] reversed,int[] family,float lighting)
    {
        Studio model=(Studio)Activator.CreateInstance(typeof(Studio),true);
        var sub=new Sub();
        var part=new Part {Name="body",Base=1};
        part.Models.Add(sub); model.Parts.Add(part);
        model.Textures.AddRange(layers);
        model.Skins=new[]{family};
        var points=new List<V3>();
        for(int i=0;i<layers.Length;i++) {
            int n=points.Count;
            points.Add(new V3(1,1,depth[i]));points.Add(new V3(1,13,depth[i]));points.Add(new V3(13,1,depth[i]));
            var a=new Corner(n,0,0,0);var b=new Corner(n+1,0,0,0);var c=new Corner(n+2,0,0,0);
            var mesh=new Mesh {SkinRef=i};
            mesh.Faces.Add(reversed[i]?new Tri(a,c,b):new Tri(a,b,c));
            sub.Meshes.Add(mesh);
        }
        var draw=new AuditGeometry(model);
        var geometry=new List<ScreenSub> {new ScreenSub {Sub=sub,Points=points.ToArray(),Light=new[]{lighting},
            ChromeU=new[]{0f},ChromeV=new[]{0f}}};
        ViewImage im=draw.Draw(new AuditView{Skin=0},geometry,new Framing{W=16,H=16,Scale=1},null);
        return im.Color[(im.Height-4-1)*im.Width+4];
    }

    static void TestBodyContext()
    {
        Studio model=(Studio)Activator.CreateInstance(typeof(Studio),true);
        model.Bones.Add(new Bone{Name="root",Parent=-1,Value=new float[6],Scale=new float[6]});
        model.Textures.Add(Material(0x974214,4,0));
        model.Skins=new[]{new[]{0}};
        var sub=new Sub { Vertices=new[]{new V3(-1,2,1),new V3(0,-1,-2),new V3(2,.5f,0)},
            VertexBones=new byte[3],Normals=new[]{new V3(0,0,1)},NormalBones=new byte[1] };
        var part=new Part{Name="body",Base=1};part.Models.Add(sub);model.Parts.Add(part);
        var mesh=new Mesh{SkinRef=0};sub.Meshes.Add(mesh);
        var x=new Corner(0,0,0,0);var y=new Corner(1,0,0,0);var z=new Corner(2,0,0,0);
        mesh.Faces.Add(new Tri(x,y,z));mesh.Faces.Add(new Tri(z,y,x));
        var audit=new VisualAudit(model,false);
        var first=audit.CompareSaved(model,null,null);
        var cached=audit.CompareSaved(model,null,null);
        Check(cached.ExactMatch && first.BodyView==cached.BodyView && cached.BodyView.StartsWith("Corpo"),
            "O contexto deve escolher a mesma vista ampla em empates, inclusive com a referência em cache.");
        string tmp=Path.GetTempPath(), token=Guid.NewGuid().ToString("N");
        string a=Path.Combine(tmp,token+"-a.png"),b=Path.Combine(tmp,token+"-b.png"),p=Path.Combine(tmp,token+"-p.png");
        try {
            cached.SaveBodyPair(a,b);cached.SaveBodyPreview(p);
            Check(Convert.ToBase64String(File.ReadAllBytes(a))==Convert.ToBase64String(File.ReadAllBytes(b)) && new FileInfo(p).Length>64,
                "O par de corpo inteiro deve guardar imagens A/A idênticas e uma prévia PNG válida.");
        } finally { File.Delete(a);File.Delete(b);File.Delete(p); }
    }

    static void TestDiagnosticPreview()
    {
        const int w=100,h=24,x=10,y=5;
        var frame=new Framing {W=w,H=h,Scale=2};
        var a=new ViewImage(w,h,frame);var b=new ViewImage(w,h,frame);
        int first=y*w+x;
        for(int i=first;i<first+8;i++) {
            a.Color[i]=b.Color[i]=0x101010;a.Owner[i]=b.Owner[i]=1;a.Depth[i]=b.Depth[i]=1;
        }
        b.Owner[first]=0;
        b.Depth[first+1]=3.75f; // 2.75 world units at this scale equals the detector threshold of 5.5 px.
        b.Depth[first+2]=3.76f;
        b.Owner[first+3]=2; // Two mesh IDs map to the same texture in this skin.
        b.Owner[first+4]=3;
        b.Owner[first+5]=3;b.Depth[first+5]=10;
        b.Color[first+6]=0xff1010;
        b.Depth[first+7]=float.NegativeInfinity;
        var result=new VisualResult {Before=a,After=b,PreviewMaterials=new[]{-1,7,7,9}};
        string path=Path.Combine(Path.GetTempPath(),Guid.NewGuid().ToString("N")+"-diagnostic.png");
        try {
            result.SavePreview(path);
            using(var png=new Bitmap(path)) {
                int originalX=8+(160-w)/2+x, optimizedX=originalX+168, mapX=optimizedX+168;
                Func<int,int> color=offset=>png.GetPixel(mapX+offset,32+y).ToArgb()&0xffffff;
                Check(color(0)==0xda293e,"A silhueta deve aparecer em vermelho.");
                Check(color(1)==0x787878 && color(2)==0x2166ac,"A profundidade azul deve respeitar o limite estrito na escala da câmera.");
                Check(color(3)==0x787878 && color(4)==0xa13ca8,"O mapa deve usar o material da família de skin e ignorar IDs equivalentes.");
                Check(color(5)==0x00845b,"A troca simultânea de profundidade e material deve aparecer em verde.");
                Check(color(6)==0xff7e10 && color(7)==0x787878,"Diferença RGB deve ficar laranja e profundidade indefinida deve ser ignorada.");
                Check((png.GetPixel(originalX+6,32+y).ToArgb()&0xffffff)==0x101010 &&
                    (png.GetPixel(optimizedX+6,32+y).ToArgb()&0xffffff)==0xff1010,"Os painéis de cor devem manter os pixels e a posição.");
            }
        } finally {File.Delete(path);}
    }

    public static int Main()
    {
        Texture baseTex=Material(0x141e28,4,0), red=Material(0xc84020,4,0);
        Check(Pixel(new[]{red},new[]{0f},new[]{false},new[]{0},.25f)==0xc84020,"Fullbright deve preservar a paleta.");
        Check(Pixel(new[]{Material(0xc84020,0,0)},new[]{0f},new[]{false},new[]{0},.25f)==0x321008,
              "A iluminação deve modular o material opaco comum.");
        Check(Pixel(new[]{red},new[]{0f},new[]{true},new[]{0},1)==ViewImage.Background,"A face invertida deve ser descartada.");
        Check(Pixel(new[]{baseTex,Material(0xff00ff,68,255)},new[]{0f,1f},new[]{false,false},new[]{0,1},1)==0x141e28,
              "O índice 255 de uma textura mascarada deve revelar a superfície de trás.");
        Check(Pixel(new[]{baseTex,Material(0xff00ff,4,255)},new[]{0f,1f},new[]{false,false},new[]{0,1},1)==0xff00ff,
              "O índice 255 de um material opaco é uma cor válida.");
        Check(Pixel(new[]{baseTex,Material(0x200000,36,1),Material(0x001000,36,1),Material(0x000080,36,1)},
              new[]{1f,3f,2f,0f},new[]{false,false,false,false},new[]{0,1,2,3},1)==0x342e28,
              "Duas camadas aditivas visíveis devem somar sem escrever no depth buffer; a camada atrás deve falhar no depth test.");
        Check(Pixel(new[]{baseTex,red},new[]{1f,0f},new[]{false,false},new[]{1,0},1)==0xc84020,
              "A família de skin deve remapear os materiais.");
        Check(Pixel(new[]{baseTex,red},new[]{0f,0f},new[]{false,false},new[]{0,1},1)==0x141e28,
              "Superfícies coplanares opacas devem usar um desempate estável.");

        var a=new ViewImage(96,96);var b=new ViewImage(96,96);
        for(int i=0;i<a.Color.Length;i++) {
            a.Color[i]=b.Color[i]=0x101010;a.Owner[i]=b.Owner[i]=1;a.Depth[i]=b.Depth[i]=1;
        }
        ViewError self=VisualAudit.Measure(a,b,new[]{-1,0,1},true,1f);
        Check(self.Pass && self.Changed==0 && self.IoU==1 && self.Mean==0,"Controle A/A precisa ser pixel-idêntico.");
        for(int y=41;y<=48;y++)for(int x=41;x<=48;x++)b.Owner[y*96+x]=2;
        ViewError local=VisualAudit.Measure(a,b,new[]{-1,0,1},true,1f);
        Check(!local.Pass && local.IoU==1 && local.Mean==0 && local.Material==64,
              "Uma troca local de superfície deve reprovar mesmo com cor e silhueta idênticas.");
        for(int y=41;y<=48;y++)for(int x=41;x<=48;x++){b.Owner[y*96+x]=1;b.Depth[y*96+x]=-15;}
        ViewError hole=VisualAudit.Measure(a,b,new[]{-1,0,1},true,1f);
        Check(!hole.Pass && hole.Material==0 && hole.Mean==0,
            "Uma mudança local de profundidade entre camadas da mesma cor/material deve reprovar.");
        TestBodyContext();
        TestDiagnosticPreview();
        Console.WriteLine("{0} verificações de renderização aprovadas.",assertions);
        return 0;
    }
}
