using System;
using System.Linq;
using System.Collections.Generic;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Mono.Cecil.Rocks;   // SimplifyMacros/OptimizeMacros: korrekte Kurz-/Langform-Branchkodierung nach IL-Edits

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

        if (mode == "ispatched")
        {
            // Robuste Erkennung, ob eine Game.dll BEREITS von uns gepatcht ist (smart ODER gated) —
            // variantenunabhängig per IL-Marker, NICHT per Byte-Vergleich. Verhindert, dass install.sh/
            // patch.sh beim Wechsel smart<->gated eine gepatchte DLL fälschlich fürs Original halten und
            // das Backup überschreiben. stdout: "patched" | "original".  Exit 0=patched, 10=original.
            // Marker: NetToolSystem hat ein EIGENES OnStopRunning (Original hat keins), dessen Rumpf
            // set_EnableBurstCompilation referenziert (smart), ODER das Feld s_ForceManaged existiert (gated).
            var net = module.Types.FirstOrDefault(t => t.FullName == "Game.Tools.NetToolSystem");
            bool patched = false;
            if (net != null)
            {
                bool hasFlag = net.Fields.Any(f => f.Name == "s_ForceManaged");           // gated
                var stop = net.Methods.FirstOrDefault(m => m.Name == "OnStopRunning" && m.Parameters.Count == 0 && m.HasBody);
                bool stopTouchesBurst = stop != null && stop.Body.Instructions.Any(i =>    // smart/bursttoggle
                    i.Operand is MethodReference mr && mr.Name == "set_EnableBurstCompilation");
                patched = hasFlag || stopTouchesBurst;
            }
            // patch-Modus fügt RunManaged_*-Stubs hinzu (kann in beliebigen Game.*-Systemen sitzen).
            // Nur top-level Game.*-Typen scannen -> schnell, erfasst alle unsere patch-Ziele.
            if (!patched)
                patched = module.Types.Any(t => t.Namespace != null && t.Namespace.StartsWith("Game.")
                    && t.Methods.Any(m => m.Name.StartsWith("RunManaged_")));
            Console.WriteLine(patched ? "patched" : "original");
            return patched ? 0 : 10;
        }

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

        if (mode == "gated")
        {
            // PERFORMANTER FIX (invertierte Logik): KEIN globaler Burst-Toggle mehr. Stattdessen ein
            // eigenes statisches Flag (NetToolSystem.s_ForceManaged), das nur gesetzt ist, solange das
            // Netz-Tool läuft. Die genannten Netz-Bau-Systeme schalten Burst dann GEZIELT nur für ihr
            // eigenes OnUpdate ab (try/finally, Zustand restaurieren). Alles andere — Simulation,
            // Rendering UND Unity-eigene ECS-Systeme (Transforms/Culling/Instancing, die NICHT in der
            // Game.dll liegen und vom Whitelist-Re-Enable des smart-Modus nie erreicht werden konnten) —
            // bleibt durchgehend auf Burst. Damit verschwindet der ~50%-FPS-Einbruch bei aktivem Tool,
            // und der Snap-Bug bleibt behoben, weil exakt dieselben Netz-Systeme wie bisher managed rechnen.
            string outG = args.Length > 2 ? args[2] : "../Game.gated.dll";
            string netList = args.Length > 3 ? args[3] : "Game.Tools.NetToolSystem";
            var (optGF, setGE) = BurstRefs(module, resolver);
            DependencyRefs(module, resolver);
            var flag = GatedToggle(module);
            int n = 0;
            foreach (var s in netList.Split(','))
            {
                var nm = s.Trim(); if (nm.Length == 0) continue;
                if (WrapOnUpdateGated(module, nm.Contains('.') ? nm : "Game.Tools." + nm, flag, optGF, _getE, setGE)) n++;
            }
            Console.WriteLine($"{n} Netz-Systeme gated-managed (Burst nur während Tool aktiv aus, sonst No-op).");
            asm.Write(outG);
            Console.WriteLine($"Geschrieben: {outG}");
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

        if (mode == "il")
        {
            // Diagnose: alle Instruktionen + Exception-Handler einer Methode ausgeben.
            //   il <in> <Type.FullName> <MethodName> [paramCount]
            string sysN = args.Length > 2 ? args[2] : "Game.Tools.DefaultToolSystem";
            string mName = args.Length > 3 ? args[3] : "OnUpdate";
            int pc = args.Length > 4 ? int.Parse(args[4]) : -1;
            var t = module.Types.First(x => x.FullName == sysN);
            foreach (var m in t.Methods.Where(m => m.Name == mName && m.HasBody && (pc < 0 || m.Parameters.Count == pc)))
            {
                Console.WriteLine($"--- {sysN}.{m.Name}({string.Join(",", m.Parameters.Select(p => p.ParameterType.Name))}) -> {m.ReturnType.Name}  (locals={m.Body.Variables.Count}, EH={m.Body.ExceptionHandlers.Count}) ---");
                foreach (var ins in m.Body.Instructions) Console.WriteLine($"  IL_{ins.Offset:X4}: {ins.OpCode} {(ins.Operand is Instruction tgt ? "IL_" + tgt.Offset.ToString("X4") : ins.Operand)}");
                foreach (var eh in m.Body.ExceptionHandlers) Console.WriteLine($"  EH {eh.HandlerType}: try IL_{eh.TryStart.Offset:X4}-IL_{eh.TryEnd.Offset:X4} handler IL_{eh.HandlerStart.Offset:X4}-IL_{(eh.HandlerEnd?.Offset ?? -1):X4}");
            }
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
        // Spec kann mit oder ohne "diag"-Platzhalter übergeben werden:
        //   patch <in> <out> "Spec"          (diag aus)
        //   patch <in> <out> diag "Spec"     (diag an)
        string spec = diag ? (args.Length > 4 ? args[4] : "SnapJob")
                           : (args.Length > 3 ? args[3] : "SnapJob");

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
            if (p.Length == 0) continue;
            string sysName, jobName;
            if (p.Contains(':'))
            {
                int ix = p.IndexOf(':');
                var s = p.Substring(0, ix);
                sysName = s.Contains('.') ? s : "Game.Tools." + s;   // voll-qualifiziert ODER bare -> Game.Tools.
                jobName = p.Substring(ix + 1);
            }
            else { sysName = "Game.Tools.NetToolSystem"; jobName = p; }
            // "Sys:*" -> alle Jobs des Systems managed; sonst der eine genannte Job.
            if (jobName == "*") { if (!PatchAllInSystem(module, sysName, diag, logRef)) return 2; }
            else if (!Patch(module, sysName, jobName, diag, logRef)) return 2;
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
    // Für den gated-Complete-in-finally: SystemBase.get_Dependency, JobHandle.Complete, JobHandle-Typ.
    static MethodReference _getDep, _jhComplete; static TypeReference _jhType;
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

    // Löst SystemBase.Dependency-Getter + JobHandle.Complete auf (für das erzwungene Ausführen der
    // Netz-Jobs im finally, SOLANGE Burst noch aus ist -> sie laufen parallel-managed, kein Crash).
    static void DependencyRefs(ModuleDefinition module, DefaultAssemblyResolver resolver)
    {
        var entitiesAsm = resolver.Resolve(module.AssemblyReferences.First(a => a.Name == "Unity.Entities"));
        var sysBase = entitiesAsm.MainModule.Types.First(t => t.FullName == "Unity.Entities.SystemBase");
        var getDepDef = sysBase.Methods.First(m => m.Name == "get_Dependency" && m.Parameters.Count == 0);
        _getDep = module.ImportReference(getDepDef);
        _jhType = module.ImportReference(getDepDef.ReturnType);                 // Unity.Jobs.JobHandle
        var jhDef = getDepDef.ReturnType.Resolve();
        _jhComplete = module.ImportReference(jhDef.Methods.First(m => m.Name == "Complete" && m.Parameters.Count == 0));
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
        // Kurzform-Branches (br.s, brtrue.s, ...) in Langform expandieren, BEVOR wir Instruktionen einfügen.
        // Sonst kann ein vorhandener Kurz-Branch, dessen Ziel an der ±127-Byte-Grenze liegt, durch unsere
        // eingefügten Bytes überlaufen -> Cecil schreibt einen korrupten Offset (Cecil expandiert NICHT
        // automatisch). OptimizeMacros() am Ende wählt wieder die kleinste GÜLTIGE Kodierung.
        body.SimplifyMacros();
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
        body.OptimizeMacros();   // Branch-Kodierung neu wählen (Kurzform wo gültig, Langform wo nötig)
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

    // Wie BurstToggle, aber statt der GLOBALEN Burst-Flagge wird nur ein eigenes statisches bool-Flag
    // (s_ForceManaged) auf NetToolSystem getoggelt: true = Netz-Tool aktiv. Die gated-Wraps lesen dieses
    // Flag und schalten Burst NUR dann (und nur für ihr eigenes OnUpdate) ab. Liefert das angelegte Feld
    // zurück, damit WrapOnUpdateGated es als Branch-Bedingung referenzieren kann.
    static FieldDefinition GatedToggle(ModuleDefinition module)
    {
        var net = module.Types.First(t => t.FullName == "Game.Tools.NetToolSystem");

        // public static bool s_ForceManaged;  (Default false -> im Normalbetrieb sind die Wraps No-ops)
        // public, damit die Wraps in anderen Typen/Namespaces (Game.Net.*, Game.Objects.*) das Feld lesen dürfen.
        var flag = new FieldDefinition("s_ForceManaged",
            FieldAttributes.Public | FieldAttributes.Static, module.TypeSystem.Boolean);
        net.Fields.Add(flag);

        // (1) OnStartRunning: ganz am Anfang s_ForceManaged = true
        var onStart = net.Methods.First(m => m.Name == "OnStartRunning" && m.Parameters.Count == 0);
        var il = onStart.Body.GetILProcessor();
        var first = onStart.Body.Instructions[0];
        il.InsertBefore(first, Instruction.Create(OpCodes.Ldc_I4_1));
        il.InsertBefore(first, Instruction.Create(OpCodes.Stsfld, flag));
        Console.WriteLine("OnStartRunning: s_ForceManaged=true injiziert");

        // (2) OnStopRunning hinzufügen: base.OnStopRunning(); s_ForceManaged = false
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
        sil.Emit(OpCodes.Ldc_I4_0);
        sil.Emit(OpCodes.Stsfld, flag);              // s_ForceManaged = false
        sil.Emit(OpCodes.Ret);
        net.Methods.Add(stop);
        Console.WriteLine($"OnStopRunning hinzugefügt (base={baseStop.DeclaringType.Name}), setzt s_ForceManaged=false");
        return flag;
    }

    // Wrappt OnUpdate() so, dass Burst NUR abgeschaltet wird, wenn das Flag (s_ForceManaged) gesetzt ist
    // (Netz-Tool aktiv). Im Normalbetrieb (Flag false) ist es ein No-op -> volle Burst-Performance.
    //   bool saved = Options.EnableBurstCompilation;          // immer sichern
    //   if (!NetToolSystem.s_ForceManaged) goto skip;         // sonst Burst gar nicht anfassen
    //   Options.EnableBurstCompilation = false;               // Netz-Bau managed rechnen lassen
    //   skip: try { <orig> } finally { Options.EnableBurstCompilation = saved; }   // Zustand restaurieren
    // Wichtig: die Sprung-Marke (skip = Nop) liegt VOR dem try-Start (origFirst). Es wird nur die
    // Burst-Abschaltung übersprungen und dann in den try-Block durchgefallen — niemals in den try-Block
    // hineingesprungen (das wäre ungültige IL).
    static bool WrapOnUpdateGated(ModuleDefinition module, string sysName, FieldReference flagField,
        FieldReference optionsField, MethodReference getEnable, MethodReference setEnable)
    {
        var sys = module.Types.FirstOrDefault(t => t.FullName == sysName);
        if (sys == null) { Console.WriteLine($"  -- {sysName}: Typ nicht gefunden"); return false; }

        // Update-Methode wählen. Zwei Konventionen in CS2:
        //   (a) Tool-Systeme (ToolBaseSystem): protected override JobHandle OnUpdate(JobHandle inputDeps)
        //       -> hier wird die Netz-Job-Pipeline (Snap/Course/...) geschedult, meist TRANSITIV über
        //          Helper (SnapControlPoints, UpdateCourse, ...). Diese Methode IMMER wrappen, auch ohne
        //          direkten Schedule-Call im Rumpf — das gated Burst-Aus deckt die Helper synchron mit ab.
        //   (b) normale GameSystemBase-Systeme: protected override void OnUpdate()  -> direkt schedulen.
        //       Nur wrappen, wenn der Rumpf wirklich Schedule/ScheduleParallel enthält (kein Nutzen sonst,
        //       und UI-/Tooltip-artige Systeme bleiben unangetastet).
        var toolUpdate = sys.Methods.FirstOrDefault(m => m.Name == "OnUpdate" && m.HasBody
            && m.Parameters.Count == 1 && m.ReturnType.Name == "JobHandle");
        var voidUpdate = sys.Methods.FirstOrDefault(m => m.Name == "OnUpdate" && m.HasBody
            && m.Parameters.Count == 0 && m.ReturnType.MetadataType == MetadataType.Void);
        var onUpdate = toolUpdate ?? voidUpdate;
        if (onUpdate == null) { Console.WriteLine($"  -- {sysName}: kein passendes OnUpdate, übersprungen"); return false; }

        // Harte Invariante: Methoden mit eigenen Exception-Handlern NICHT wrappen (verschachteltes
        // try/finally korrumpiert die IL -> InvalidProgram/Crash).
        if (onUpdate.Body.HasExceptionHandlers) { Console.WriteLine($"  -- {sysName}: {onUpdate.Name} hat eigene try/catch, übersprungen (Sicherheit)"); return false; }
        // Bei der void-Variante zusätzlich verlangen, dass wirklich Jobs geschedult werden.
        if (toolUpdate == null)
        {
            bool schedulesJobs = onUpdate.Body.Instructions.Any(i =>
                (i.OpCode == OpCodes.Call || i.OpCode == OpCodes.Callvirt) && i.Operand is MethodReference mr
                && (mr.Name == "Schedule" || mr.Name == "ScheduleParallel"));
            if (!schedulesJobs) { Console.WriteLine($"  -- {sysName}: kein Job-Schedule in OnUpdate, übersprungen (kein Nutzen)"); return false; }
        }

        var body = onUpdate.Body;
        // Kurzform-Branches expandieren, bevor wir Instruktionen einfügen (siehe Kommentar in WrapOnUpdate):
        // sonst überlaufen Branches an der ±127-Byte-Grenze -> korrupte Offsets. OptimizeMacros() am Ende.
        body.SimplifyMacros();
        var il = body.GetILProcessor();
        var origFirst = body.Instructions[0];

        body.InitLocals = true;
        var saved = new VariableDefinition(module.TypeSystem.Boolean);
        body.Variables.Add(saved);

        // Rückgabewert berücksichtigen: bei JobHandle-Variante muss der Rückgabewert über das finally
        // hinweg gerettet werden (ein nacktes `leave` würde den Eval-Stack leeren und den Wert verwerfen).
        bool isVoid = onUpdate.ReturnType.MetadataType == MetadataType.Void;
        VariableDefinition retVal = null;
        if (!isVoid) { retVal = new VariableDefinition(onUpdate.ReturnType); body.Variables.Add(retVal); }
        // void-Systeme: lokale JobHandle-Variable, um this.Dependency zu halten und im finally zu Complete()-n.
        VariableDefinition depTmp = null;
        if (isVoid) { depTmp = new VariableDefinition(_jhType); body.Variables.Add(depTmp); }

        // Ziel nach dem finally:  (void) ret  |  (non-void) ldloc retVal; ret
        var retIns = Instruction.Create(OpCodes.Ret);
        var afterFinally = isVoid ? retIns : Instruction.Create(OpCodes.Ldloc, retVal);

        // alle ret -> (stloc retVal;) leave afterFinally
        foreach (var ins in body.Instructions.Where(i => i.OpCode == OpCodes.Ret).ToList())
        {
            if (isVoid) { ins.OpCode = OpCodes.Leave; ins.Operand = afterFinally; }
            else { ins.OpCode = OpCodes.Stloc; ins.Operand = retVal; il.InsertAfter(ins, Instruction.Create(OpCodes.Leave, afterFinally)); }
        }

        // finally: ERST (falls Flag gesetzt) die geschedulten Netz-Jobs JETZT ausführen — solange Burst
        // noch AUS ist. Sie laufen damit parallel-managed (kein Single-Thread-Crash wie beim patch-Modus)
        // und managed-korrekt (kein Timing-Problem: ohne dieses Complete liefen sie erst NACH dem Restore
        // wieder auf Burst). DANACH Burst restaurieren.
        //   if (s_ForceManaged) { tool: retVal.Complete()  |  void: this.Dependency.Complete() }
        //   Options.EnableBurstCompilation = saved;
        var fStart = Instruction.Create(OpCodes.Ldsfld, flagField);
        il.Append(fStart);
        var restoreStart = Instruction.Create(OpCodes.Ldsfld, optionsField);
        il.Append(Instruction.Create(OpCodes.Brfalse, restoreStart));
        if (!isVoid)
        {
            il.Append(Instruction.Create(OpCodes.Ldloca, retVal));
            il.Append(Instruction.Create(OpCodes.Call, _jhComplete));     // retVal.Complete()
        }
        else
        {
            il.Append(Instruction.Create(OpCodes.Ldarg_0));
            il.Append(Instruction.Create(OpCodes.Callvirt, _getDep));     // this.Dependency
            il.Append(Instruction.Create(OpCodes.Stloc, depTmp));
            il.Append(Instruction.Create(OpCodes.Ldloca, depTmp));
            il.Append(Instruction.Create(OpCodes.Call, _jhComplete));     // .Complete()
        }
        il.Append(restoreStart);                                          // Options...
        il.Append(Instruction.Create(OpCodes.Ldloc, saved));
        il.Append(Instruction.Create(OpCodes.Callvirt, setEnable));       // = saved
        il.Append(Instruction.Create(OpCodes.Endfinally));
        il.Append(afterFinally);
        if (!isVoid) il.Append(retIns);

        // Prologue (vor try): saved = Options.EBC; if (!flag) goto skip; Options.EBC = false; skip:
        var skip = Instruction.Create(OpCodes.Nop);
        foreach (var ins in new[]{
            Instruction.Create(OpCodes.Ldsfld, optionsField),
            Instruction.Create(OpCodes.Callvirt, getEnable),
            Instruction.Create(OpCodes.Stloc, saved),
            Instruction.Create(OpCodes.Ldsfld, flagField),
            Instruction.Create(OpCodes.Brfalse, skip),
            Instruction.Create(OpCodes.Ldsfld, optionsField),
            Instruction.Create(OpCodes.Ldc_I4_0),
            Instruction.Create(OpCodes.Callvirt, setEnable),
            skip,
        }) il.InsertBefore(origFirst, ins);

        body.ExceptionHandlers.Add(new ExceptionHandler(ExceptionHandlerType.Finally)
        {
            TryStart = origFirst,
            TryEnd = fStart,
            HandlerStart = fStart,
            HandlerEnd = afterFinally,
        });
        body.OptimizeMacros();   // Branch-Kodierung neu wählen (Kurzform wo gültig, Langform wo nötig)
        Console.WriteLine($"  ++ {sysName}.{onUpdate.Name}({(isVoid ? "" : "JobHandle")}) gated-managed");
        return true;
    }

    // MethodReference einer Methode auf einer generischen Instanz (z.B. NativeList<E>::get_Length)
    static MethodReference OnGeneric(MethodReference self, TypeReference declaring, ModuleDefinition module)
    {
        var r = new MethodReference(self.Name, self.ReturnType, declaring)
        { HasThis = self.HasThis, ExplicitThis = self.ExplicitThis, CallingConvention = self.CallingConvention };
        foreach (var p in self.Parameters) r.Parameters.Add(new ParameterDefinition(p.ParameterType));
        return module.ImportReference(r);
    }

    // Patcht ALLE Jobs, die ein System schedult (Wildcard "Sys:*"). Sammelt die distinct nested Job-Structs
    // aus allen Schedule/ScheduleParallel-Calls des Systems und biegt jeden auf einen managed Stub um.
    // Best-effort: einzelne nicht-patchbare Jobs (z.B. mehrere Schedule-Sites) werden geloggt, nicht abgebrochen.
    static bool PatchAllInSystem(ModuleDefinition module, string sysName, bool diag, MethodReference logRef)
    {
        var sys = module.Types.FirstOrDefault(t => t.FullName == sysName);
        if (sys == null) { Console.WriteLine($"  -- {sysName}: Typ nicht gefunden"); return false; }
        var jobNames = new List<string>();
        foreach (var m in sys.Methods)
        {
            if (m.Body == null) continue;
            foreach (var ins in m.Body.Instructions)
                if (ins.Operand is GenericInstanceMethod gim
                    && (gim.ElementMethod.Name == "Schedule" || gim.ElementMethod.Name == "ScheduleParallel")
                    && gim.ElementMethod.DeclaringType.Name.EndsWith("Extensions")
                    && gim.GenericArguments.Count >= 1)
                {
                    var jn = gim.GenericArguments[0].Name;
                    // nur nested Job-Structs DIESES Systems (Stub-Erzeugung erwartet ein nested struct)
                    if (sys.NestedTypes.Any(nt => nt.Name == jn) && !jobNames.Contains(jn)) jobNames.Add(jn);
                }
        }
        if (jobNames.Count == 0) { Console.WriteLine($"  -- {sysName}: keine eigenen Jobs geschedult"); return true; }
        int ok = 0;
        foreach (var jn in jobNames) if (Patch(module, sysName, jn, diag, logRef)) ok++;
        Console.WriteLine($"  == {sysName}: {ok}/{jobNames.Count} Jobs managed");
        return true;
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
