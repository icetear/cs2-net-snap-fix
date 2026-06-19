using System;
using System.Linq;
using System.Collections.Generic;
using Mono.Cecil;
using Mono.Cecil.Cil;

// Cecil-Tool: zwingt Burst-Jobs auf den managed Pfad, indem der Schedule-Call umgebogen wird.
//   IJob       : Schedule<T>(job, deps)            -> RunManaged: deps.Complete(); job.Execute(); return deps;
//   IJobChunk  : Schedule/ScheduleParallel<T>(job, query, deps)
//                -> RunManaged: deps.Complete(); JobChunkExtensions.RunByRefWithoutJobs(ref job, query); return deps;
//
// Aufruf: patcher patch <in> <out> [diag] "System1:Job1,System2:Job2,..."
//   System ohne ":" -> NetToolSystem.<Job> (Abwärtskompat). diag = Debug.Log-Marker.
class Program
{
    static int Main(string[] args)
    {
        string mode = args.Length > 0 ? args[0] : "dump";
        string input = args.Length > 1 ? args[1] : "../Game.dll";

        string managedDir = Environment.GetEnvironmentVariable("CS2_MANAGED")
            ?? System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Library/Application Support/CrossOver/Bottles/Steam/drive_c/Program Files (x86)/Steam/steamapps/common/Cities Skylines II/Cities2_Data/Managed");
        var resolver = new DefaultAssemblyResolver();
        resolver.AddSearchDirectory(managedDir);
        resolver.AddSearchDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(input)));
        var asm = AssemblyDefinition.ReadAssembly(input, new ReaderParameters { AssemblyResolver = resolver });
        var module = asm.MainModule;

        if (mode == "bursttoggle")
        {
            string outBt = args.Length > 2 ? args[2] : "../Game.bursttoggle.dll";
            BurstToggle(module, resolver);
            asm.Write(outBt);
            Console.WriteLine($"Geschrieben: {outBt}");
            return 0;
        }

        if (mode == "smart")
        {
            // Globaler Toggle (Burst aus während Netz-Tool) + Burst für angegebene (schwere, nicht-netz)
            // Systeme während ihres OnUpdate WIEDER EIN (invertiertes Wrap) => Sim auf Burst, Netz-Preview managed.
            string outS = args.Length > 2 ? args[2] : "../Game.smart.dll";
            string reEnable = args.Length > 3 ? args[3] : "";
            var (of, se) = BurstRefs(module, resolver);
            BurstToggle(module, resolver);
            int n = 0;
            foreach (var s in reEnable.Split(','))
            {
                var nm = s.Trim(); if (nm.Length == 0) continue;
                if (WrapOnUpdate(module, nm.Contains('.') ? nm : "Game.Simulation." + nm, of, se, enableInside: true)) n++;
            }
            Console.WriteLine($"{n} Systeme re-enabled (Burst an während ihres Updates).");
            asm.Write(outS);
            Console.WriteLine($"Geschrieben: {outS}");
            return 0;
        }

        if (mode == "wrap")
        {
            // Burst chirurgisch nur während OnUpdate der genannten Systeme abschalten (try/finally).
            string outW = args.Length > 2 ? args[2] : "../Game.wrap.dll";
            string sysList = args.Length > 3 ? args[3] : "CourseSplitSystem";
            var (optionsField, setEnable) = BurstRefs(module, resolver);
            foreach (var s in sysList.Split(','))
            {
                var nm = s.Trim();
                if (nm.Length == 0) continue;
                WrapOnUpdate(module, nm.Contains('.') ? nm : "Game.Tools." + nm, optionsField, setEnable);
            }
            asm.Write(outW);
            Console.WriteLine($"Geschrieben: {outW}");
            return 0;
        }

        if (mode == "dump")
        {
            string sys = args.Length > 2 ? args[2] : "Game.Tools.NetToolSystem";
            var t = module.Types.First(x => x.FullName == sys);
            foreach (var m in t.Methods)
                foreach (var ins in m.Body?.Instructions ?? Enumerable.Empty<Instruction>())
                    if (ins.Operand is GenericInstanceMethod g
                        && (g.ElementMethod.Name == "Schedule" || g.ElementMethod.Name == "ScheduleParallel")
                        && g.ElementMethod.DeclaringType.Name.EndsWith("Extensions"))
                        Console.WriteLine($"{m.Name}: {g.ElementMethod.DeclaringType.Name}.{g.ElementMethod.Name}<{g.GenericArguments[0].Name}> @ IL_{ins.Offset:X4}");
            return 0;
        }

        string outp = args.Length > 2 ? args[2] : "../Game.patched.dll";
        bool diag = args.Length > 3 && args[3] == "diag";
        string spec = args.Length > 4 ? args[4] : "SnapJob";

        MethodReference logRef = null;
        if (diag)
        {
            var core = module.AssemblyReferences.First(a => a.Name == "UnityEngine.CoreModule");
            var debugType = new TypeReference("UnityEngine", "Debug", module, core);
            logRef = new MethodReference("Log", module.TypeSystem.Void, debugType) { HasThis = false };
            logRef.Parameters.Add(new ParameterDefinition(module.TypeSystem.Object));
            logRef = module.ImportReference(logRef);
        }

        foreach (var part in spec.Split(','))
        {
            var p = part.Trim();
            string sysName = p.Contains(':') ? "Game.Tools." + p.Split(':')[0] : "Game.Tools.NetToolSystem";
            string jobName = p.Contains(':') ? p.Split(':')[1] : p;
            if (!Patch(module, sysName, jobName, diag, logRef)) return 2;
        }

        asm.Write(outp);
        Console.WriteLine($"Geschrieben: {outp}");
        return 0;
    }

    // Schaltet Burst global ab, solange das Netz-Tool aktiv ist:
    //  OnStartRunning -> EnableBurstCompilation=false ; OnStopRunning -> =true
    // => alle Netz-Bau-Jobs laufen dann managed (korrekt unter Rosetta), kein Job wird synchron ausgeführt.
    // Löst die Unity.Burst-Referenzen auf: BurstCompiler.Options (Feld) + get/set_EnableBurstCompilation.
    static FieldReference _optF; static MethodReference _getE, _setE;
    static (FieldReference, MethodReference) BurstRefs(ModuleDefinition module, DefaultAssemblyResolver resolver)
    {
        var burstAsm = resolver.Resolve(module.AssemblyReferences.First(a => a.Name == "Unity.Burst"));
        var optType = burstAsm.MainModule.Types.First(t => t.FullName == "Unity.Burst.BurstCompilerOptions");
        var compType = burstAsm.MainModule.Types.First(t => t.FullName == "Unity.Burst.BurstCompiler");
        _optF = module.ImportReference(compType.Fields.First(f => f.Name == "Options"));
        _getE = module.ImportReference(optType.Methods.First(m => m.Name == "get_EnableBurstCompilation"));
        _setE = module.ImportReference(optType.Methods.First(m => m.Name == "set_EnableBurstCompilation"));
        return (_optF, _setE);
    }

    // Wrappt OnUpdate() mit try/finally. enableInside=false: Burst AUS während Update (Default).
    // enableInside=true: Burst AN während Update, danach wieder AUS (für globalen-Toggle-Kontext).
    static bool WrapOnUpdate(ModuleDefinition module, string sysName, FieldReference optionsField, MethodReference setEnable, bool enableInside = false)
    {
        var sys = module.Types.FirstOrDefault(t => t.FullName == sysName);
        if (sys == null) { Console.WriteLine($"  -- {sysName}: Typ nicht gefunden"); return false; }
        var onUpdate = sys.Methods.FirstOrDefault(m => m.Name == "OnUpdate" && m.Parameters.Count == 0 && m.HasBody);
        if (onUpdate == null) { Console.WriteLine($"  -- {sysName}: kein eigenes OnUpdate, übersprungen"); return false; }
        // Sicherheit: OnUpdate mit vorhandenen Exception-Handlern NICHT wrappen (verschachteltes try/finally
        // korrumpiert sonst die IL -> InvalidProgram/Crash). Solche Systeme bleiben unverändert.
        if (onUpdate.Body.HasExceptionHandlers) { Console.WriteLine($"  -- {sysName}: OnUpdate hat eigene try/catch, übersprungen (Sicherheit)"); return false; }
        // Nur Systeme wrappen, die tatsächlich Burst-Jobs schedulen (sonst kein FPS-Nutzen + unnötiges Risiko).
        bool schedulesJobs = onUpdate.Body.Instructions.Any(i =>
            (i.OpCode == OpCodes.Call || i.OpCode == OpCodes.Callvirt) && i.Operand is MethodReference mr
            && (mr.Name == "Schedule" || mr.Name == "ScheduleParallel"));
        if (!schedulesJobs) { Console.WriteLine($"  -- {sysName}: kein Job-Schedule in OnUpdate, übersprungen (kein FPS-Nutzen)"); return false; }
        var body = onUpdate.Body;
        var il = body.GetILProcessor();
        var origFirst = body.Instructions[0];
        var startVal = enableInside ? OpCodes.Ldc_I4_1 : OpCodes.Ldc_I4_0;

        // lokale bool-Variable für den vorherigen Zustand (save/restore!)
        body.InitLocals = true;
        var saved = new VariableDefinition(module.TypeSystem.Boolean);
        body.Variables.Add(saved);

        // alle ret -> leave afterFinally
        var afterFinally = Instruction.Create(OpCodes.Ret);
        foreach (var ins in body.Instructions.Where(i => i.OpCode == OpCodes.Ret).ToList())
        { ins.OpCode = OpCodes.Leave; ins.Operand = afterFinally; }

        // finally: Options.EnableBurstCompilation = saved;  (Zustand WIEDERHERSTELLEN, nicht hart setzen)
        var fStart = Instruction.Create(OpCodes.Ldsfld, optionsField);
        il.Append(fStart);
        il.Append(Instruction.Create(OpCodes.Ldloc, saved));
        il.Append(Instruction.Create(OpCodes.Callvirt, _setE));
        il.Append(Instruction.Create(OpCodes.Endfinally));
        il.Append(afterFinally);

        // Prologue (vor try): saved = Options.EnableBurstCompilation; Options.EnableBurstCompilation = startVal;
        foreach (var ins in new[]{
            Instruction.Create(OpCodes.Ldsfld, optionsField),
            Instruction.Create(OpCodes.Callvirt, _getE),
            Instruction.Create(OpCodes.Stloc, saved),
            Instruction.Create(OpCodes.Ldsfld, optionsField),
            Instruction.Create(startVal),
            Instruction.Create(OpCodes.Callvirt, setEnable),
        }) il.InsertBefore(origFirst, ins);

        body.ExceptionHandlers.Add(new ExceptionHandler(ExceptionHandlerType.Finally)
        {
            TryStart = origFirst,
            TryEnd = fStart,
            HandlerStart = fStart,
            HandlerEnd = afterFinally,
        });
        return true;
    }

    static void BurstToggle(ModuleDefinition module, DefaultAssemblyResolver resolver)
    {
        var net = module.Types.First(t => t.FullName == "Game.Tools.NetToolSystem");
        var (optionsField, setEnable) = BurstRefs(module, resolver);

        // (1) OnStartRunning: ganz am Anfang Burst aus
        var onStart = net.Methods.First(m => m.Name == "OnStartRunning" && m.Parameters.Count == 0);
        var il = onStart.Body.GetILProcessor();
        var first = onStart.Body.Instructions[0];
        il.InsertBefore(first, Instruction.Create(OpCodes.Ldsfld, optionsField));
        il.InsertBefore(first, Instruction.Create(OpCodes.Ldc_I4_0));
        il.InsertBefore(first, Instruction.Create(OpCodes.Callvirt, setEnable));
        Console.WriteLine("OnStartRunning: EnableBurstCompilation=false injiziert");

        // (2) OnStopRunning hinzufügen (base aufrufen + Burst an). Basis-OnStopRunning suchen.
        MethodReference baseStop = null;
        for (var bt = net.BaseType; bt != null; )
        {
            var btd = bt.Resolve();
            var m = btd.Methods.FirstOrDefault(x => x.Name == "OnStopRunning" && x.Parameters.Count == 0);
            if (m != null) { baseStop = module.ImportReference(m); break; }
            bt = btd.BaseType;
        }
        if (baseStop == null) throw new Exception("base OnStopRunning nicht gefunden");

        var stop = new MethodDefinition("OnStopRunning",
            MethodAttributes.Family | MethodAttributes.HideBySig | MethodAttributes.Virtual,
            module.TypeSystem.Void);
        var sil = stop.Body.GetILProcessor();
        sil.Emit(OpCodes.Ldarg_0);
        sil.Emit(OpCodes.Call, baseStop);            // base.OnStopRunning()
        sil.Emit(OpCodes.Ldsfld, optionsField);
        sil.Emit(OpCodes.Ldc_I4_1);
        sil.Emit(OpCodes.Callvirt, setEnable);       // Options.EnableBurstCompilation = true
        sil.Emit(OpCodes.Ret);
        net.Methods.Add(stop);
        Console.WriteLine($"OnStopRunning hinzugefügt (base={baseStop.DeclaringType.Name})");
    }

    // MethodReference einer Methode auf einer generischen Instanz (z.B. NativeList<E>::get_Length)
    static MethodReference OnGeneric(MethodReference self, TypeReference declaring, ModuleDefinition module)
    {
        var r = new MethodReference(self.Name, self.ReturnType, declaring)
        { HasThis = self.HasThis, ExplicitThis = self.ExplicitThis, CallingConvention = self.CallingConvention };
        foreach (var p in self.Parameters) r.Parameters.Add(new ParameterDefinition(p.ParameterType));
        return module.ImportReference(r);
    }

    static bool Patch(ModuleDefinition module, string sysName, string jobName, bool diag, MethodReference logRef)
    {
        var sys = module.Types.First(t => t.FullName == sysName);
        var jobType = sys.NestedTypes.First(t => t.Name == jobName);
        bool isChunk = jobType.Interfaces.Any(i => i.InterfaceType.Name == "IJobChunk");
        bool isDefer = jobType.Interfaces.Any(i => i.InterfaceType.Name == "IJobParallelForDefer");

        // Schedule/ScheduleParallel<jobType>-Call in irgendeiner Methode des Systems finden
        MethodDefinition host = null; Instruction call = null; GenericInstanceMethod schedRef = null;
        foreach (var m in sys.Methods)
        {
            if (m.Body == null) continue;
            foreach (var ins in m.Body.Instructions)
            {
                if (ins.OpCode == OpCodes.Call && ins.Operand is GenericInstanceMethod gim
                    && (gim.ElementMethod.Name == "Schedule" || gim.ElementMethod.Name == "ScheduleParallel")
                    && gim.ElementMethod.DeclaringType.Name.EndsWith("Extensions")
                    && gim.GenericArguments.Count >= 1
                    && gim.GenericArguments[0].Name == jobName)
                {
                    if (call != null) { Console.WriteLine($"FEHLER: mehrere Schedule<{jobName}>!"); return false; }
                    host = m; call = ins; schedRef = gim;
                }
            }
        }
        if (call == null)
        {
            Console.WriteLine($"FEHLER: Schedule<{jobName}> nicht gefunden in {sysName}! Kandidaten:");
            foreach (var m in sys.Methods)
                foreach (var ins in m.Body?.Instructions ?? Enumerable.Empty<Instruction>())
                    if (ins.Operand is GenericInstanceMethod g && (g.ElementMethod.Name == "Schedule" || g.ElementMethod.Name == "ScheduleParallel"))
                        Console.WriteLine($"   op={ins.OpCode} decl={g.ElementMethod.DeclaringType.Name} gargs={g.GenericArguments.Count} arg0={(g.GenericArguments.Count>0?g.GenericArguments[0].Name:"-")}");
            return false;
        }

        TypeReference jobHandleRef = schedRef.Parameters[schedRef.Parameters.Count - 1].ParameterType; // letzter Param = JobHandle
        var completeRef = module.ImportReference(new MethodReference("Complete", module.TypeSystem.Void, jobHandleRef) { HasThis = true });

        var run = new MethodDefinition("RunManaged_" + jobName,
            MethodAttributes.Private | MethodAttributes.Static | MethodAttributes.HideBySig, jobHandleRef);
        run.Body.InitLocals = false;
        var il = run.Body.GetILProcessor();

        ParameterDefinition pJob = new ParameterDefinition("job", ParameterAttributes.None, (TypeReference)jobType);
        run.Parameters.Add(pJob);
        ParameterDefinition pArg1 = null, pArg2 = null, pDeps;
        if (isChunk)
        {
            pArg1 = new ParameterDefinition("query", ParameterAttributes.None, schedRef.Parameters[1].ParameterType); // EntityQuery
            run.Parameters.Add(pArg1);
        }
        else if (isDefer)
        {
            pArg1 = new ParameterDefinition("list", ParameterAttributes.None, schedRef.Parameters[1].ParameterType);  // NativeList<E>
            pArg2 = new ParameterDefinition("batch", ParameterAttributes.None, schedRef.Parameters[2].ParameterType); // int
            run.Parameters.Add(pArg1);
            run.Parameters.Add(pArg2);
        }
        pDeps = new ParameterDefinition("deps", ParameterAttributes.None, jobHandleRef);
        run.Parameters.Add(pDeps);

        if (diag) { il.Emit(OpCodes.Ldstr, "[SNAPPATCH] RunManaged_" + jobName); il.Emit(OpCodes.Call, logRef); }

        il.Emit(OpCodes.Ldarga_S, pDeps);
        il.Emit(OpCodes.Call, completeRef);

        if (isChunk)
        {
            // JobChunkExtensions.RunByRefWithoutJobs<jobType>(ref job, query)
            var jce = schedRef.ElementMethod.DeclaringType.Resolve();
            var runDef = jce.Methods.First(m => m.Name == "RunByRefWithoutJobs" && m.Parameters.Count == 2);
            var gim2 = new GenericInstanceMethod(module.ImportReference(runDef));
            gim2.GenericArguments.Add(jobType);
            il.Emit(OpCodes.Ldarga_S, pJob);
            il.Emit(OpCodes.Ldarg, pArg1);
            il.Emit(OpCodes.Call, gim2);
        }
        else if (isDefer)
        {
            // for (int i=0; i<list.Length; i++) job.Execute(i);   (managed, kein Burst)
            var execute = jobType.Methods.First(m => m.Name == "Execute" && m.Parameters.Count == 1);
            var listGI = (GenericInstanceType)pArg1.ParameterType;
            var getLen = OnGeneric(listGI.Resolve().Methods.First(m => m.Name == "get_Length"), listGI, module);
            run.Body.InitLocals = true;
            var vN = new VariableDefinition(module.TypeSystem.Int32);
            var vI = new VariableDefinition(module.TypeSystem.Int32);
            run.Body.Variables.Add(vN); run.Body.Variables.Add(vI);
            il.Emit(OpCodes.Ldarga_S, pArg1); il.Emit(OpCodes.Call, getLen); il.Emit(OpCodes.Stloc, vN);
            il.Emit(OpCodes.Ldc_I4_0); il.Emit(OpCodes.Stloc, vI);
            var check = Instruction.Create(OpCodes.Ldloc, vI);
            var body = Instruction.Create(OpCodes.Ldarga_S, pJob);
            il.Emit(OpCodes.Br, check);
            il.Append(body);                                  // job.Execute(i)
            il.Emit(OpCodes.Ldloc, vI); il.Emit(OpCodes.Call, execute);
            il.Emit(OpCodes.Ldloc, vI); il.Emit(OpCodes.Ldc_I4_1); il.Emit(OpCodes.Add); il.Emit(OpCodes.Stloc, vI);
            il.Append(check);                                 // i < n ?
            il.Emit(OpCodes.Ldloc, vN); il.Emit(OpCodes.Blt, body);
        }
        else
        {
            // job.Execute()
            var execute = jobType.Methods.First(m => m.Name == "Execute" && m.Parameters.Count == 0);
            il.Emit(OpCodes.Ldarga_S, pJob);
            il.Emit(OpCodes.Call, execute);
        }

        il.Emit(OpCodes.Ldarg, pDeps);
        il.Emit(OpCodes.Ret);
        sys.Methods.Add(run);

        call.Operand = run;
        Console.WriteLine($"OK: {sysName}.{jobName} ({(isChunk ? "IJobChunk" : "IJob")}) -> {run.Name} (in {host.Name} @ IL_{call.Offset:X4})");
        return true;
    }
}
