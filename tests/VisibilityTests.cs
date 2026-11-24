using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MdlOptimizer;

internal static class VisibilityTests
{
    static int checks;
    static void Check(bool ok,string message)
    {
        if(!ok)throw new InvalidOperationException(message);
        checks++;
    }
    static Tri Face(int a,int b,int c)
    {
        return new Tri(new Corner(a,0,0,0),new Corner(b,0,0,0),new Corner(c,0,0,0));
    }
    static Sub Geometry(V3[] points,int material)
    {
        var sub=new Sub {Vertices=points,Normals=new[]{new V3(0,0,1)},VertexBones=new byte[points.Length],NormalBones=new byte[]{0}};
        sub.Meshes.Add(new Mesh {SkinRef=material});
        return sub;
    }
    static Texture Texture(int flag,byte pixel) {
        var palette=new byte[768];
        for(int i=0;i<palette.Length;i++)palette[i]=128;
        return new Texture{Name="test",Flags=flag,Width=1,Height=1,Pixels=new[]{pixel},Palette=palette};
    }
    static int Main(string[] args)
    {
        try {
            Studio s=Studio.Load(Directory.GetFiles(args[0],"*.mdl").First());
            s.Parts.Clear();s.Bones.Clear();s.Sequences.Clear();s.Textures.Clear();
            s.Bones.Add(new Bone{Name="root",Parent=-1,Value=new float[6],Scale=new float[6]});
            s.Textures.Add(Texture(0,0));s.Textures.Add(Texture(32,0));s.Textures.Add(Texture(64,255));s.Textures.Add(Texture(0,0));
            s.Skins=new[]{new[]{0,1,3}};
            var points=new V3[8];
            for(int i=0;i<8;i++)points[i]=new V3((i&1)*2-1,((i>>1)&1)*2-1,((i>>2)&1)*2-1);
            var shell=Geometry(points,0);Mesh outer=shell.Meshes[0];
            int[][] sides={new[]{1,3,7,5},new[]{0,4,6,2},new[]{2,6,7,3},new[]{0,1,5,4},new[]{4,5,7,6},new[]{0,2,3,1}};
            foreach(int[] side in sides){outer.Faces.Add(Face(side[0],side[2],side[1]));outer.Faces.Add(Face(side[0],side[3],side[2]));}
            var inner=Geometry(new[]{new V3(-.2f,-.2f,0),new V3(.2f,-.2f,0),new V3(0,.2f,0),
                new V3(1.05f,.101f,.101f),new V3(1.05f,.1011f,.101f),new V3(1.05f,.101f,.1011f)},2);
            Mesh hidden=inner.Meshes[0];hidden.Faces.Add(Face(0,1,2));hidden.Faces.Add(Face(3,4,5));
            var additive=new Mesh{SkinRef=1};additive.Faces.Add(Face(0,1,2));inner.Meshes.Add(additive);
            var skin=new Part{Name="shell",Base=1};skin.Models.Add(shell);s.Parts.Add(skin);
            var inside=new Part{Name="inner",Base=1};inside.Models.Add(inner);s.Parts.Add(inside);

            var exact=Simplifier.Exact(s);
            var seen=VisibilityMap.Sample(s,exact,null);
            Check(seen[outer].SetEquals(Enumerable.Range(0,12)),"Todas as faces externas do cubo deveriam receber amostras");
            Check(!seen[hidden].Contains(0),"Interior completamente fechado classificado como exposto");
            Check(seen[hidden].Contains(1),"Uma face externa menor que um pixel foi tratada como oculta");
            Check(seen[additive].Contains(0),"O mapa deve proteger efeitos aditivos");
            var reordered=new Dictionary<Mesh,List<Tri>>(exact);
            reordered[hidden]=exact[hidden].AsEnumerable().Reverse().ToList();
            Check(VisibilityMap.Sample(s,reordered,null)[hidden].SetEquals(new[]{0}),"Os índices devem acompanhar a ordem da lista exata fornecida");
            var again=VisibilityMap.Sample(s,exact,null);
            Check(seen.All(p=>again[p.Key].SetEquals(p.Value)),"Mapa não determinístico");
            s.Skins=new[]{new[]{0,1,3},new[]{2,1,3}};
            Check(VisibilityMap.Sample(s,exact,null)[hidden].Contains(0),"Outra família com material recortado deveria revelar o interior");
            s.Skins=new[]{new[]{0,1,3}};
            skin.Models.Add(Geometry(new V3[0],0));
            Check(VisibilityMap.Sample(s,Simplifier.Exact(s),null)[hidden].Contains(0),"O interior deve ser visto com bodygroup opcional desligado");
            bool cancelled=false; try { VisibilityMap.Sample(s,exact,()=>true); } catch(OperationCanceledException) { cancelled=true; }
            Check(cancelled,"Medição de visibilidade ignorou cancelamento");
            Console.WriteLine("{0} verificações do mapa de visibilidade aprovadas.",checks);
            return 0;
        } catch(Exception e){Console.Error.WriteLine(e);return 1;}
    }
}
