using System;
using System.Collections.Generic;
using System.Linq;
using MdlOptimizer;

internal static class PatchAtlasTests
{
    static int checks;
    static void Check(bool passed,string message)
    {
        checks++;
        if(!passed)throw new InvalidOperationException(message);
    }

    static Corner C(int vertex) {return new Corner(vertex,0,vertex,0);}
    static Tri T(int a,int b,int c) {return new Tri(C(a),C(b),C(c));}
    static List<Tri> Fan(int a,int b,int c,int center)
    {
        return new List<Tri>{T(a,b,center),T(b,c,center),T(c,a,center)};
    }
    static Dictionary<Mesh,List<Tri>> One(Mesh mesh,IEnumerable<Tri> faces)
    {
        return new Dictionary<Mesh,List<Tri>>{{mesh,faces.ToList()}};
    }
    static void Reject(List<Tri> before,List<Tri> after,string label)
    {
        var mesh=new Mesh();
        var atlas=new PatchAtlas(One(mesh,before),One(mesh,after));
        Check(atlas.Patches.Count==0,label);
        Check(atlas.Apply(new HashSet<PatchAtlas.Patch>())[mesh].SequenceEqual(before),
            "A rejeição alterou a malha de referência");
    }
    static void IndependentRegions()
    {
        var mesh=new Mesh();var secondMaterial=new Mesh();
        var baseline=One(mesh,Fan(0,1,2,3).Concat(Fan(1,0,4,5)));
        Tri common=T(10,11,12);
        baseline[mesh].Insert(2,common);
        baseline[secondMaterial]=Fan(0,1,2,3);
        var proposal=One(mesh,new[]{T(1,2,0),T(11,12,10),T(1,0,4)});
        proposal[secondMaterial]=new List<Tri>{T(0,1,2)};
        var atlas=new PatchAtlas(baseline,proposal);
        Check(atlas.Patches.Count==3,"As duas regiões adjacentes e o segundo material devem ser independentes");
        foreach(var p in atlas.Patches) {
            Check(p.Saving==2,"Economia incorreta");
            var selected=atlas.Apply(new HashSet<PatchAtlas.Patch>{p});
            Check(selected.Values.Sum(s=>s.Count)==baseline.Values.Sum(s=>s.Count)-2,"Contagem parcial incorreta");
            Check(selected[mesh].Contains(common),"A troca alterou uma face comum");
            Check(selected[p.Mesh].Contains(p.After[0]),"Faltou o novo triângulo");
            var other=p.Mesh==mesh?secondMaterial:mesh;
            Check(Object.ReferenceEquals(selected[other],baseline[other]),"A troca alterou outro material");
        }
        var all=atlas.Apply(new HashSet<PatchAtlas.Patch>(atlas.Patches));
        Check(all[mesh].Count==3 && all[secondMaterial].Count==1,"A combinação total perdeu uma região");
        Check(baseline[mesh].Count==7 && baseline[secondMaterial].Count==3,"A combinação modificou sua referência");
    }
    static void InvalidRegions()
    {
        var disk=Fan(0,1,2,3);
        Reject(disk,new List<Tri>{T(0,2,1)},"O contorno invertido foi aceito");
        Reject(disk,new List<Tri>{new Tri(new Corner(0,0,17,0),C(1),C(2))},"A costura UV foi ignorada");
        Reject(disk,new List<Tri>{new Tri(new Corner(0,1,0,0),C(1),C(2))},"A descontinuidade de normal foi ignorada");
        Reject(disk,new List<Tri>{T(8,1,2)},"A troca mudou um vértice de contorno");
        Reject(disk.Concat(Fan(0,1,2,4)).ToList(),new List<Tri>{T(0,1,2),T(0,1,2)},"Contornos duplicados foram aceitos");
        Reject(disk.Concat(new[]{T(3,0,5),T(0,3,5)}).ToList(),new List<Tri>{T(0,1,2)},
            "Aresta interna com quatro faces foi aceita");

        var tetra=new List<Tri>{T(0,2,1),T(0,1,3),T(1,2,3),T(2,0,3)};
        var closed=Fan(0,2,1,4).Concat(tetra.Skip(1)).ToList();
        Reject(closed,new List<Tri>{T(7,9,8),T(7,8,10),T(8,9,10),T(9,7,10)},
            "Duas superfícies fechadas sem interface foram trocadas");

        // A torus punctured at one triangle and a disk have the same single
        // contour. Substituting one for the other would fill its handle.
        const int width=4;
        var torus=new List<Tri>();
        for(int y=0;y<width;y++)for(int x=0;x<width;x++) {
            int a=width*y+x,b=width*y+(x+1)%width;
            int d=width*((y+1)%width)+x,c=width*((y+1)%width)+(x+1)%width;
            torus.Add(T(a,b,c));torus.Add(T(a,c,d));
        }
        Tri opening=torus[0];torus.RemoveAt(0);
        Reject(torus,new List<Tri>{new Tri(opening.C,opening.B,opening.A)},
            "Contornos iguais com característica de Euler diferente foram trocados");
    }
    static int Main()
    {
        try {
            IndependentRegions();InvalidRegions();
            Console.WriteLine("{0} verificações de contornos de malha aprovadas.",checks);
            return 0;
        } catch(Exception e){Console.Error.WriteLine(e);return 1;}
    }
}
