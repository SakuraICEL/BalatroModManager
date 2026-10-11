using System;
using System.Collections.Generic;
using BMM;

internal static class PortableTests
{
    private static int count;
    private static void Check(bool condition, string name)
    {
        if (!condition) throw new Exception(name);
        count++;
    }
    public static int Main()
    {
        try
        {
            Check(Ver.Parse("2.0").CompareTo(Ver.Parse("1.9")) > 0, "version order");
            Check(Ver.Parse("v1.2.3").CompareTo(Ver.Parse("1.2.3")) == 0, "version prefix");
            Check(Ver.Parse("1.0.0").CompareTo(Ver.Parse("1.0.0-beta")) > 0, "release follows prerelease");
            Check(Ver.Parse("1.0.0-alpha.2").CompareTo(Ver.Parse("1.0.0-alpha.10")) < 0, "numeric prerelease order");
            Check(Ver.Parse("1.0.0+build.1").CompareTo(Ver.Parse("1.0.0+build.2")) == 0, "build metadata ignored");
            Check(Ver.Parse("0.5.17c").CompareTo(Ver.Parse("0.5.17")) > 0, "legacy letter suffix preserved");
            Check(Ver.Parse("99999999999999999999").CompareTo(Ver.Parse("2")) > 0, "large number against small number");
            Check(Ver.Parse("100000000000000000000").CompareTo(Ver.Parse("99999999999999999999")) > 0, "large number order");
            Check(Ver.Parse("000100000000000000000000").CompareTo(Ver.Parse("100000000000000000000")) == 0, "numeric leading zeros");
            foreach (string name in new string[] { @"..\..\escape", "../escape", @"C:\outside", "..", "CON", "name.", "name ", "dir/name", "dir\\name" })
            {
                bool zipRejected = false, luaRejected = false;
                try { ModLibrary.InstallZip("unused.zip", name, true); } catch (ArgumentException) { zipRejected = true; }
                try { ModLibrary.InstallLua("unused.lua", name, true); } catch (ArgumentException) { luaRejected = true; }
                Check(zipRejected && luaRejected, "unsafe install name rejected before I/O");
            }
            DepSpec dependency = DepSpec.Parse("ExampleLibrary (>=1.0) (<<2.0)");
            Check(dependency.Name == "ExampleLibrary", "dependency name");
            Check(dependency.Test(Ver.Parse("1.5")), "dependency accepted");
            Check(!dependency.Test(Ver.Parse("2.5")), "dependency rejected");
            List<TsPackage> packages = new List<TsPackage>();
            packages.Add(new TsPackage { Name = "ExampleAlpha", Owner = "ExampleAuthor", Description = "card theme", Downloads = 20 });
            packages.Add(new TsPackage { Name = "ExampleBeta", Owner = "ExampleAuthor", Description = "new jokers", Downloads = 100 });
            Check(Thunderstore.Search(packages, "card theme", "全部", true).Count == 1, "search");
            Check(Thunderstore.Sort(packages, "下载量")[0].Name == "ExampleBeta", "sort");
            Console.WriteLine("PASS: " + count + " portable assertions. No local game data or network access.");
            return 0;
        }
        catch (Exception ex) { Console.WriteLine("FAIL: " + ex.Message); return 1; }
    }
}
