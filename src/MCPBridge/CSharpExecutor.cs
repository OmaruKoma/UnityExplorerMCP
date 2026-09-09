using System;
using System.IO;
using System.Reflection;
using Mono.CSharp;
using UnityEngine;

namespace UnityExplorer.MCPBridge
{
    /// <summary>
    /// REPL base class exposed to executed snippets: Log(x) writes to the Unity log.
    /// Mirrors UnityExplorer's ScriptInteraction minimal surface (no UI dependency).
    /// </summary>
    public class CSharpReplBase : InteractiveBase
    {
        public static void Log(object message)
        {
            Debug.Log("[MCP-C#] " + (message != null ? message.ToString() : "null"));
        }
    }

    /// <summary>
    /// P0: self-hosted Mono.CSharp evaluator (same mcs.dll engine UnityExplorer's
    /// C# Console uses). Runs on the Unity main thread via MCPBridge's request
    /// queue, so no il2cpp_thread_attach is needed on this path.
    /// </summary>
    public static class CSharpExecutor
    {
        private static readonly object _lock = new object();
        private static Evaluator _eval;
        private static StringWriter _output;
        private static StreamReportPrinter _printer;
        private static bool _available;
        private static string _disableReason;

        static CSharpExecutor()
        {
            Init();
        }

        private static void Init()
        {
            try
            {
                Reset();
                _available = true;
            }
            catch (Exception ex)
            {
                _available = false;
                _disableReason = ex.ToString();
            }
        }

        private static void Reset()
        {
            _output = new StringWriter();
            _printer = new StreamReportPrinter(_output);
            var settings = new CompilerSettings
            {
                Version = LanguageVersion.Experimental,
                GenerateDebugInfo = false,
                StdLib = true,
                Target = Target.Library,
                WarningLevel = 0,
                EnhancedWarnings = false,
                Unsafe = true
            };
            var ctx = new CompilerContext(settings, _printer);
            _eval = new Evaluator(ctx)
            {
                InteractiveBaseClass = typeof(CSharpReplBase)
            };
            foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try { _eval.ReferenceAssembly(asm); }
                catch { }
            }
            AppDomain.CurrentDomain.AssemblyLoad += (s, e) =>
            {
                try { _eval.ReferenceAssembly(e.LoadedAssembly); }
                catch { }
            };
            // Default usings, same flavour as UnityExplorer's C# Console.
            _eval.Run("using System;");
            _eval.Run("using System.Linq;");
            _eval.Run("using System.Text;");
            _eval.Run("using System.Collections;");
            _eval.Run("using System.Collections.Generic;");
            _eval.Run("using System.Reflection;");
            _eval.Run("using UnityEngine;");
            _output.GetStringBuilder().Length = 0;
        }

        public class ExecResult
        {
            public bool Compiled;
            public object ReturnValue;
            public string ReturnType;
            public string CompilerOutput;
            public string Error;
        }

        public static ExecResult Execute(string code, string returnEncoding = "hex")
        {
            if (!_available)
                return new ExecResult { Compiled = false, Error = "C# evaluator unavailable. " + _disableReason };

            lock (_lock)
            {
                try
                {
                    _output.GetStringBuilder().Length = 0;
                    int errorsBefore = _printer.ErrorsCount;

                    CompiledMethod repl = null;
                    try
                    {
                        repl = _eval.Compile(code);
                    }
                    catch (Exception ex)
                    {
                        return new ExecResult
                        {
                            Compiled = false,
                            Error = ex.ToString(),
                            CompilerOutput = _output.ToString()
                        };
                    }

                    if (repl != null)
                    {
                        object ret = null;
                        try
                        {
                            repl.Invoke(ref ret);
                        }
                        catch (Exception ex)
                        {
                            return new ExecResult
                            {
                                Compiled = true,
                                Error = ex.ToString(),
                                CompilerOutput = _output.ToString()
                            };
                        }
                        return new ExecResult
                        {
                            Compiled = true,
                            ReturnValue = ValueSerializer.Serialize(ret, returnEncoding),
                            ReturnType = ret != null ? ret.GetType().FullName : "null",
                            CompilerOutput = _output.ToString()
                        };
                    }

                    string output = _output.ToString();
                    if (_printer.ErrorsCount > errorsBefore)
                        return new ExecResult { Compiled = false, Error = "Compile errors:\n" + output, CompilerOutput = output };
                    return new ExecResult
                    {
                        Compiled = true,
                        ReturnValue = "Code compiled without errors (using directive or class definition).",
                        ReturnType = "System.String",
                        CompilerOutput = output
                    };
                }
                catch (Exception ex)
                {
                    return new ExecResult { Compiled = false, Error = ex.ToString() };
                }
            }
        }
    }
}
