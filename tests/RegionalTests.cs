using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using MdlOptimizer;

internal static class RegionalTests
{
    static int checks;
    static void Check(bool ok,string message)
    {
        if(!ok)throw new InvalidOperationException(message);
        checks++;
    }

    static Texture Plain(string name)
    {
        return new Texture{Name=name,Width=32,Height=32,Pixels=new byte[32*32],Palette=new byte[256*3]};
    }

    static Corner UV(int x,int y) { return new Corner(0,0,x,y); }

    static RegionalSimplifier.ColorField Field(Studio s,Mesh m)
    {
        return new RegionalSimplifier.ColorField(s,m,new RegionalSimplifier.ColorField.Tables());
    }

    static void Colors(Studio s)
    {
        s.Textures.Clear();
        var baseTex=Plain("body");var altTex=Plain("winter");
        s.Textures.Add(baseTex);s.Textures.Add(altTex);
        s.Skins=new[]{new[]{0},new[]{1}};
        var m=new Mesh{SkinRef=0};
        var a=UV(3,3);var b=UV(8,3);var c=UV(3,8);
        Check(Field(s,m).Get(a,b,c,a)==0,"Preto uniforme nas duas skins");
        altTex.Pixels[2+2*32]=5;
        Check(Field(s,m).Get(a,b,c,a)<0,"O filtro bilinear alcança um índice diferente na borda");
        altTex.Pixels[2+2*32]=0;
        for(int y=0;y<32;y++)for(int x=15;x<32;x++)altTex.Pixels[y*32+x]=7;
        var field=Field(s,m);
        Check(field.Get(a,b,c,a)==0,"A faixa com índices diferentes está fora da região inicial");
        Check(field.Get(a,UV(20,3),c,a)<0,"Uma skin alternativa tem uma mudança de índice dentro da região proposta");
        Check(field.Get(UV(20,4),UV(25,4),UV(20,8),a)<0,"Índices diferentes da mesma cor foram confundidos");
        Check(field.Get(a,UV(31,3),c,a)<0 && field.Get(UV(-2,3),b,c,a)<0,"Certificado em UVs repetidos/fora da imagem");
        baseTex.Palette[0]=23;altTex.Palette[0]=23;
        Check(Field(s,m).Get(a,b,c,a)==23<<16,"Amostra uniforme colorida perdeu a cor");
        altTex.Palette[0]=24;
        Check(Field(s,m).Get(a,b,c,a)<0,"A família alternativa usa outra cor");
        altTex.Palette[0]=23;
        foreach(int flags in new[]{2,16,32,64}) {
            altTex.Flags=flags;
            Check(Field(s,m).Get(a,b,c,a)<0,"Atalho em uma textura com efeito da skin alternativa");
        }
        altTex.Flags=0;
        foreach(string name in new[]{"DM_Base.bmp","remap_casaco.bmp"}) {
            altTex.Name=name;
            Check(Field(s,m).Get(a,b,c,a)<0,"Atalho em uma textura remapeável");
        }
        altTex.Name="winter";
        var tiny=new RegionalSimplifier.ColorField.Tables {Remaining=8L*33*33};
        Check(new RegionalSimplifier.ColorField(s,m,tiny).Get(a,b,c,a)<0,"Mapa de cores parcial após esgotar o orçamento");
        s.Skins=new[]{new[]{0},new[]{0}};
        tiny=new RegionalSimplifier.ColorField.Tables {Remaining=8L*33*33};
        Check(new RegionalSimplifier.ColorField(s,m,tiny).Get(a,b,c,a)==23<<16 && tiny.Remaining==0,
            "Uma textura usada em várias famílias deve gastar memória uma única vez");
    }

    static void Topology(Studio s)
    {
        const int width=12;
        s.Textures.Clear();s.Textures.Add(Plain("body"));s.Textures.Add(Plain("alt"));
        s.Skins=new[]{new[]{0},new[]{1}};
        s.Bones.Clear();s.Bones.Add(new Bone{Name="root",Parent=-1,Value=new float[6],Scale=new float[6]});
        var sub=new Sub{Vertices=new V3[width*width],VertexBones=new byte[width*width],
            Normals=new[]{new V3(0,0,1)},NormalBones=new byte[]{0}};
        var m=new Mesh{SkinRef=0};sub.Meshes.Add(m);
        var part=new Part{Name="body",Base=1};
        part.Models.Add(sub);s.Parts.Clear();s.Parts.Add(part);
        var uv=new Corner[width*width];
        for(int v=0;v<uv.Length;v++) {
            int x=v%width,y=v/width;
            sub.Vertices[v]=new V3(x,y,(float)(.09*Math.Sin(x*1.3)*Math.Cos(y*1.2)));
            uv[v]=new Corner(v,0,2+2*x,2+2*y);
        }
        for(int y=0;y<width-1;y++)for(int x=0;x<width-1;x++) {
            int v=y*width+x;
            m.Faces.Add(new Tri(uv[v],uv[v+1],uv[v+width+1]));
            m.Faces.Add(new Tri(uv[v],uv[v+width+1],uv[v+width]));
        }
        var exact=Simplifier.Exact(s);
        var visible=new Dictionary<Mesh,HashSet<int>>{{m,new HashSet<int>(Enumerable.Range(0,exact[m].Count))}};
        var hidden=new Dictionary<Mesh,HashSet<int>>{{m,new HashSet<int>()}};
        var seen=RegionalSimplifier.Reduce(s,exact,visible,1,null)[m];
        var unseen=RegionalSimplifier.Reduce(s,exact,hidden,1,null)[m];
        Check(unseen.Count<seen.Count && seen.Count<=exact[m].Count,
            String.Format("Contagens da superfície ondulada, vista {0}, oclusa {1}, original {2}",seen.Count,unseen.Count,exact[m].Count));
        Check(unseen.Count>0,"Uma ilha jamais vista desapareceu completamente");
        Check(RegionalSimplifier.Reduce(s,exact,hidden,1,null)[m].SequenceEqual(unseen),"A mesma entrada produziu triângulos diferentes");
        var allPinned=new Dictionary<Mesh,HashSet<int>>{{m,new HashSet<int>(Enumerable.Range(0,width*width))}};
        Check(RegionalSimplifier.Reduce(s,exact,hidden,4,allPinned)[m].SequenceEqual(exact[m]),"Uma região protegida foi modificada");
        s.Textures[1].Flags=32;exact=Simplifier.Exact(s);
        Check(RegionalSimplifier.Reduce(s,exact,hidden,4,null)[m].SequenceEqual(exact[m]),"Uma skin aditiva alternativa foi simplificada");
        s.Textures[1].Flags=0;
        var back=m.Faces.Select(t=>new Tri(t.C,t.B,t.A)).ToArray();
        m.Faces.AddRange(back);exact=Simplifier.Exact(s);
        Check(RegionalSimplifier.Reduce(s,exact,hidden,4,null)[m].SequenceEqual(exact[m]),"As camadas de uma superfície de dupla face foram modificadas");
        foreach(int invalid in new[]{0,RegionalSimplifier.ModeCount+1}) {
            bool threw=false;
            try{RegionalSimplifier.Reduce(s,exact,hidden,invalid,null);}catch(ArgumentOutOfRangeException){threw=true;}
            Check(threw,"Perfil regional inválido foi aceito");
        }
    }

    static int Main(string[] args)
    {
        try {
            if(args.Length!=1)throw new ArgumentException("Informe a pasta com os modelos de teste.");
            string fixture=Directory.GetFiles(args[0],"*.mdl").OrderBy(f=>f,StringComparer.OrdinalIgnoreCase).First();
            var studio=Studio.Load(fixture);Colors(studio);Topology(studio);
            Console.WriteLine("{0} verificações regionais aprovadas.",checks);return 0;
        }
        catch(Exception e){Console.Error.WriteLine(e);return 1;}
    }
}
