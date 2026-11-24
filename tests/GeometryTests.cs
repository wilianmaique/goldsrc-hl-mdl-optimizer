using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Security.Cryptography;
using MdlOptimizer;

// Standalone regression checks against the public API. The small synthetic
// surfaces isolate edge cases that are hard to recognize in a rendered player.
internal static class GeometryTests
{
    static int checks;

    static void Check(bool condition,string label)
    {
        if (!condition) throw new InvalidOperationException(label);
        checks++;
    }

    static string Digest(string file)
    {
        using (var input=File.OpenRead(file))
        using (var hash=SHA256.Create())
            return Convert.ToBase64String(hash.ComputeHash(input));
    }

    static void Fixture(string path)
    {
        string hash=Digest(path);
        Studio s=Studio.Load(path);
        var exact=Simplifier.Exact(s);
        var known=s.Parts.SelectMany(p=>p.Models).SelectMany(sub=>sub.Meshes).ToList();
        Check(known.All(m=>exact.ContainsKey(m) && exact[m].Count<=m.Faces.Count),"Cobertura da cópia exata");
        int total=exact.Values.Sum(t=>t.Count);
        var results=new int[Simplifier.ModeCount];
        Dictionary<Mesh,List<Tri>> previous=null;
        for (int level=1;level<=Simplifier.ModeCount;level++)
        {
            var reduced=Simplifier.Reduce(s,exact,level,null);
            results[level-1]=reduced.Values.Sum(t=>t.Count);
            Check(results[level-1]<=total,"O modo de jogo aumentou a contagem");
            foreach (Mesh m in known)
            {
                var sourceCorners=new HashSet<Corner>();
                foreach (Tri t in m.Faces) { sourceCorners.Add(t.A); sourceCorners.Add(t.B); sourceCorners.Add(t.C); }
                Check(reduced[m].All(t=>sourceCorners.Contains(t.A) && sourceCorners.Contains(t.B) && sourceCorners.Contains(t.C)),
                    "Uma redução produziu atributos fora da malha original");
            }
            previous=reduced;
        }
        var repeated=Simplifier.Reduce(s,exact,Simplifier.ModeCount,null);
        Check(known.All(m=>repeated[m].SequenceEqual(previous[m])),"Resultado variável com a mesma entrada");
        var protectedResult=Simplifier.Reduce(s,exact,Simplifier.ModeCount,new HashSet<Mesh>(known));
        Check(known.All(m=>protectedResult[m].SequenceEqual(exact[m])),"Uma malha protegida foi modificada");

        string temp=Path.Combine(Path.GetTempPath(),"mdlopt-geometry-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            string copy=Path.Combine(temp,"teste.mdl");
            s.Save(copy,previous,false);
            Studio opened=Studio.Load(copy);
            s.VerifyMetadata(opened);
            Check(opened.TriangleCount==results[results.Length-1],"Contagem incorreta após gravar e reabrir");
            string savedHash=Digest(copy);
            bool refused=false;
            try { opened.Save(copy,Simplifier.Exact(opened),true); } catch (IOException) { refused=true; }
            Check(refused && Digest(copy)==savedHash,"A gravação substituiu o próprio modelo");
        }
        finally { Directory.Delete(temp,true); }
        Check(hash==Digest(path),"O arquivo original foi modificado");
        Console.WriteLine("{0}  original {1}, exato {2}, candidatos {3}",Path.GetFileName(path),s.TriangleCount,total,String.Join("/",results));
    }

    static Sub Synthetic(Studio s,int vertices)
    {
        s.Parts.Clear(); s.Textures.Clear(); s.Bones.Clear();
        s.Skins=new[] {new[] {0,1}};
        for(int i=0;i<2;i++)
        {
            s.Bones.Add(new Bone {Name="Bone"+i,Parent=-1,Value=new float[6],Scale=new float[6]});
            s.Textures.Add(new Texture {Name="material"+i,Width=256,Height=256});
        }
        var sub=new Sub {Vertices=new V3[vertices],Normals=new[] {new V3(0,0,1),new V3(0,1,0),new V3(0,0,1),new V3(0,0,1)},
                         VertexBones=new byte[vertices],NormalBones=new byte[] {0,0,1,0}};
        sub.Meshes.Add(new Mesh {SkinRef=0});
        var part=new Part {Name="body",Base=1}; part.Models.Add(sub); s.Parts.Add(part);
        return sub;
    }

    static List<Tri> Clean(Studio s,Mesh mesh,params Tri[] triangles)
    {
        mesh.Faces=new List<Tri>(triangles);
        return Simplifier.Exact(s)[mesh];
    }

    static void ExactCases(Studio s)
    {
        Sub sub=Synthetic(s,5);
        sub.Vertices[1]=new V3(1,0,0); sub.Vertices[2]=new V3(0,1,0);
        sub.VertexBones[3]=1;
        Mesh m=sub.Meshes[0];
        Corner a=new Corner(0,0,0,0), b=new Corner(1,0,8,0), c=new Corner(2,0,0,8);
        Tri t=new Tri(a,b,c), reverse=new Tri(c,b,a);
        Check(Clean(s,m,t,new Tri(b,c,a),new Tri(new Corner(4,3,0,0),b,c)).SequenceEqual(new[]{t}),"Duplicatas por atributos reais");
        Check(Clean(s,m,t,reverse).SequenceEqual(new[]{t,reverse}),"O verso desapareceu");
        foreach(Corner variant in new[] { new Corner(0,0,12,0),new Corner(0,1,0,0),new Corner(0,2,0,0),new Corner(3,0,0,0) })
        {
            Tri diff=new Tri(variant,b,c);
            Check(Clean(s,m,t,diff).SequenceEqual(new[]{t,diff}),"UV, normal ou osso diferente foi descartado");
        }
        Tri overlay=new Tri(new Corner(0,0,12,0),b,c);
        Check(Clean(s,m,t,overlay,t).SequenceEqual(new[]{t,overlay,t}),"A ordem de uma sobreposição foi alterada");
        Tri zero=new Tri(a,new Corner(4,0,0,0),c), opening=new Tri(a,new Corner(3,0,0,0),c);
        Check(Clean(s,m,zero,opening).SequenceEqual(new[]{opening}),"A face que pode abrir numa animação foi removida");

        foreach (int flags in new[] {16,32,64})
        {
            s.Textures[0].Flags=flags;
            Check(Clean(s,m,t,t).Count==2,"Camada com efeito foi deduplicada");
        }
        s.Textures[0].Flags=0; s.Textures[1].Flags=32;
        s.Skins=new[]{new[]{0,1},new[]{1,0}};
        Check(Clean(s,m,t,t).Count==2,"Material aditivo de uma skin alternativa foi descartado");
        s.Textures[1].Flags=0;
        var second=new Mesh {SkinRef=1}; second.Faces.Add(t); sub.Meshes.Add(second);
        m.Faces=new List<Tri>{t};
        var result=Simplifier.Exact(s);
        Check(result[m].Count==1 && result[second].Count==1,"Malhas de materiais diferentes foram unidas");
    }

    static long Key(int a,int b) { return ((long)Math.Min(a,b)<<32)|(uint)Math.Max(a,b); }

    static HashSet<long> Boundaries(IEnumerable<Tri> tris)
    {
        var counts=new Dictionary<long,int>();
        foreach(Tri t in tris) for(int j=0;j<3;j++)
        {
            long edge=Key(t[j].V,t[(j+1)%3].V);
            int n; counts.TryGetValue(edge,out n); counts[edge]=n+1;
        }
        Check(counts.Values.All(n=>n==1 || n==2),"Uma aresta não manifold foi criada");
        return new HashSet<long>(counts.Where(p=>p.Value==1).Select(p=>p.Key));
    }

    static void Grid(Studio s)
    {
        const int w=10;
        Sub sub=Synthetic(s,2*w*w);
        var corners=new Corner[w*w]; Mesh mesh=sub.Meshes[0];
        for(int i=0;i<w*w;i++)
        {
            sub.Vertices[i]=new V3(i%w,i/w,0);
            sub.Vertices[w*w+i]=sub.Vertices[i];
            corners[i]=new Corner(i,0,(i%w)*8,(i/w)*8);
        }
        for(int y=0;y<w-1;y++) for(int x=0;x<w-1;x++)
        {
            int a=w*y+x; mesh.Faces.Add(new Tri(corners[a],corners[a+1],corners[a+w+1]));
            mesh.Faces.Add(new Tri(corners[a],corners[a+w+1],corners[a+w]));
        }
        var exact=Simplifier.Exact(s);
        var sourceEdges=Boundaries(mesh.Faces);
        var reduced=Simplifier.Reduce(s,exact,4,null)[mesh];
        Check(reduced.Count<mesh.Faces.Count,"A grade plana fechada não foi simplificada");
        Check(sourceEdges.SetEquals(Boundaries(reduced)),"A redução abriu uma fenda na grade");
        foreach(Tri t in reduced)
        {
            V3 a=sub.Vertices[t.A.V],b=sub.Vertices[t.B.V],c=sub.Vertices[t.C.V];
            Check((b.X-a.X)*(c.Y-a.Y)>(b.Y-a.Y)*(c.X-a.X),"Face invertida ou colapsada");
        }

        const int center=44;
        var pinned=new Dictionary<Mesh,HashSet<int>>{{mesh,new HashSet<int>{center}}};
        var repaired=Simplifier.Reduce(s,exact,4,null,pinned)[mesh];
        var originalCenter=mesh.Faces.Where(t=>t.A.V==center || t.B.V==center || t.C.V==center);
        Check(originalCenter.All(t=>repaired.Contains(t)),"A região selecionada por vértice deixou de ser exata");

        var history=AdaptiveGeometry.Trace(s,exact,4);
        Check(history.Faces[mesh].SequenceEqual(reduced),"Registrar as operações mudou a redução inicial");
        int previous=reduced.Count,partials=0;
        for(int j=0;j<w*w;j++)
        {
            int id=(j*37+center)%(w*w);
            var sample=new Dictionary<Mesh,HashSet<int>>{{mesh,new HashSet<int>{id}}};
            var restored=history.Repair(sample);
            if(restored==null)continue;
            List<Tri> faces=restored[mesh];
            Check(faces.Count>previous,"A reversão local deixou de restaurar triângulos");
            Check(sourceEdges.SetEquals(Boundaries(faces)),"A reversão local abriu uma fenda");
            var directed=new HashSet<long>();
            foreach(Tri t in faces)
            {
                for(int k=0;k<3;k++)Check(directed.Add(((long)t[k].V<<32)|(uint)t[(k+1)%3].V),
                    "A reversão criou arestas com orientação conflitante");
                V3 a=sub.Vertices[t.A.V],b=sub.Vertices[t.B.V],c=sub.Vertices[t.C.V];
                Check((b.X-a.X)*(c.Y-a.Y)>(b.Y-a.Y)*(c.X-a.X),"A reversão local inverteu uma face");
            }
            Check(history.Repair(sample)==null,"Reverter o mesmo conjunto alterou novamente a malha");
            if(faces.Count<mesh.Faces.Count)partials++;
            previous=faces.Count;
        }
        Check(partials>0,"A reversão de um vértice restaurou a malha inteira");
        Check(history.Faces[mesh].SequenceEqual(exact[mesh]),"Reverter todos os colapsos não restaurou as faces e atributos");

        // Some compilers emit several equivalent position/bone indices for one
        // surface point. A pixel on either incident triangle can blame either ID.
        var split=new List<Tri>();
        Func<Corner,Corner> alias=c=>new Corner(c.V+w*w,c.N,c.U,c.T);
        for(int i=0;i<mesh.Faces.Count;i++) {
            Tri t=mesh.Faces[i];
            split.Add(i%2==0?t:new Tri(alias(t.A),alias(t.B),alias(t.C)));
        }
        var alternateIds=AdaptiveGeometry.Trace(s,new Dictionary<Mesh,List<Tri>>{{mesh,split}},4);
        Check(alternateIds.Faces[mesh].Count==reduced.Count,"Os índices equivalentes mudaram o resultado da simplificação");
        var aliasFeedback=new Dictionary<Mesh,HashSet<int>>{{mesh,new HashSet<int>(Enumerable.Range(w*w,w*w))}};
        var aliasRepair=alternateIds.Repair(aliasFeedback);
        Check(aliasRepair!=null && aliasRepair[mesh].SequenceEqual(split),"A reversão por um índice equivalente perdeu os índices originais");

        Tri sheet=mesh.Faces[80]; Tri back=new Tri(sheet.C,sheet.B,sheet.A);
        mesh.Faces.Add(back); exact=Simplifier.Exact(s);
        var protectedBack=Simplifier.Reduce(s,exact,4,null)[mesh];
        Check(protectedBack.Contains(sheet) && protectedBack.Contains(back),"Uma das faces de uma superfície dupla sumiu");
        Check(Simplifier.MilderLevel(1)==0 && Simplifier.MilderLevel(5)==0 && Simplifier.MilderLevel(8)==7,"Relação entre perfis");
        Check(Enumerable.Range(1,Simplifier.ModeCount).Select(Simplifier.ProfileName).Distinct().Count()==Simplifier.ModeCount,
            "Nomes de perfis duplicados");
        foreach(int invalid in new[]{0,Simplifier.ModeCount+1})
        {
            bool threw=false;
            try { Simplifier.Reduce(s,exact,invalid,null); } catch(ArgumentOutOfRangeException) { threw=true; }
            Check(threw,"Perfil inválido aceito");
        }
    }

    static int Main(string[] args)
    {
        try
        {
            if(args.Length!=1) throw new ArgumentException("Informe a pasta dos modelos de teste.");
            var fixtures=Directory.GetFiles(args[0],"*.mdl").OrderBy(s=>s,StringComparer.OrdinalIgnoreCase).ToArray();
            if(fixtures.Length==0) throw new ArgumentException("Nenhum MDL na pasta de teste.");
            Studio synthetic=Studio.Load(fixtures[0]);
            ExactCases(synthetic); Grid(synthetic);
            foreach(string path in fixtures) Fixture(path);
            Console.WriteLine("{0} verificações de geometria aprovadas.",checks);
            return 0;
        }
        catch(Exception e) { Console.Error.WriteLine(e); return 1; }
    }
}
