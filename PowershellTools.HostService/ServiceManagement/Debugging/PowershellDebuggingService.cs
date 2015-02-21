﻿using EnvDTE80;
using Microsoft.PowerShell;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Management.Automation;
using System.Management.Automation.Host;
using System.Management.Automation.Runspaces;
using System.Reflection;
using System.ServiceModel;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using PowerShellTools.Common.ServiceManagement.DebuggingContract;
using System.Text.RegularExpressions;
using PowerShellTools.Common.Debugging;

namespace PowerShellTools.HostService.ServiceManagement.Debugging
{
    [ServiceBehavior(ConcurrencyMode = ConcurrencyMode.Multiple)]
    [PowerShellServiceHostBehavior]
    public partial class PowershellDebuggingService : IPowershellDebuggingService
    {
        private static Runspace _runspace;
        private PowerShell _currentPowerShell;
        private IDebugEngineCallback _callback;
        private string _debuggingCommand;
        private IEnumerable<PSObject> _varaiables;
        private IEnumerable<PSObject> _callstack;
        private string log;
        private Collection<PSVariable> _localVariables;
        private Dictionary<string, Object> _propVariables;
        private Dictionary<string, string> _mapLocalToRemote;
        private Dictionary<string, string> _mapRemoteToLocal;
        private readonly AutoResetEvent _pausedEvent = new AutoResetEvent(false);

        public PowershellDebuggingService()
        {
            ServiceCommon.Log("Initializing debugging engine service ...");
            HostUi = new HostUi(this);
            _localVariables = new Collection<PSVariable>();
            _propVariables = new Dictionary<string, object>();
            _mapLocalToRemote = new Dictionary<string, string>();
            _mapRemoteToLocal = new Dictionary<string, string>();
            InitializeRunspace(this);
        }

        /// <summary>
        /// The runspace used by the current PowerShell host.
        /// </summary>
        public static Runspace Runspace 
        {
            get
            {
                return _runspace;
            }
            set
            {
                _runspace = value;
            }
        }

        public IDebugEngineCallback CallbackService
        {
            get
            {
                return _callback;
            }
            set 
            {
                _callback = value;
            }
        }

        #region Debugging service calls

        /// <summary>
        /// Initialize of powershell runspace
        /// </summary>
        public void SetRunspace(bool overrideExecutionPolicy)
        {
            if (overrideExecutionPolicy)
            {
                SetupExecutionPolicy();
            }

            SetRunspace(_runspace);
        }

        /// <summary>
        /// Client respond with resume action to service
        /// </summary>
        /// <param name="action">Resumeaction from client</param>
        public void ExecuteDebuggingCommand(string debuggingCommand)
        {
            ServiceCommon.Log("Client respond with debugging command");
            _debuggingCommand = debuggingCommand;
            _pausedEvent.Set();
        }

        /// <summary>
        /// Sets breakpoint for the current runspace.
        /// </summary>
        /// <param name="bp">Breakpoint to set</param>
        public void SetBreakpoint(PowershellBreakpoint bp)
        {
            ServiceCommon.Log("Setting breakpoint ...");
            
            using (var pipeline = (_runspace.CreatePipeline()))
            {
                var command = new Command("Set-PSBreakpoint");

                string file = bp.ScriptFullPath;
                if (_runspace.ConnectionInfo != null && _mapLocalToRemote.ContainsKey(bp.ScriptFullPath))
                {
                    file = _mapLocalToRemote[bp.ScriptFullPath];
                }
                
                command.Parameters.Add("Script", file);
                
                command.Parameters.Add("Line", bp.Line);

                pipeline.Commands.Add(command);

                pipeline.Invoke();
            }
        }

        /// <summary>
        /// Clears existing breakpoints for the current runspace.
        /// </summary>
        public void ClearBreakpoints()
        {
            ServiceCommon.Log("ClearBreakpoints");

            IEnumerable<PSObject> breakpoints;

            using (var pipeline = (_runspace.CreatePipeline()))
            {
                var command = new Command("Get-PSBreakpoint");
                pipeline.Commands.Add(command);
                breakpoints = pipeline.Invoke();
            }

            if (!breakpoints.Any()) return;

            using (var pipeline = (_runspace.CreatePipeline()))
            {
                var command = new Command("Remove-PSBreakpoint");
                command.Parameters.Add("Breakpoint", breakpoints);
                pipeline.Commands.Add(command);

                pipeline.Invoke();
            }
        }

        /// <summary>
        /// Execute the specified command line from client
        /// </summary>
        /// <param name="commandLine">Command line to execute</param>
        public bool Execute(string commandLine)
        {
            if (_runspace.ConnectionInfo != null && Regex.IsMatch(commandLine, DebugEngineConstants.ExecutionCommandPattern))
            {
                Regex rgx = new Regex(DebugEngineConstants.ExecutionCommandFileReplacePattern);
                string localFile = rgx.Match(commandLine).Value;
                commandLine = rgx.Replace(commandLine, _mapLocalToRemote[localFile]);
            }

            ServiceCommon.Log("Start executing ps script ...");
            
            try
            {
                if (_callback == null)
                {
                    _callback = OperationContext.Current.GetCallbackChannel<IDebugEngineCallback>();
                }
            }
            catch (Exception)
            {
                ServiceCommon.Log("No instance context retrieved.");
            }

            if (_runspace.RunspaceAvailability != RunspaceAvailability.Available)
            {
                _callback.OutputString("Pipeline not executed because a pipeline is already executing. Pipelines cannot be executed concurrently.");
                return false;
            }

            bool error = false;
            try
            {
                // only do this when we are working with a local runspace
                if (_runspace.ConnectionInfo == null)
                {
                    // Preset dte as PS variable if not yet
                    if (_runspace.SessionStateProxy.PSVariable.Get("dte") == null)
                    {
                        DTE2 dte = DTEManager.GetDTE(Program.VsProcessId);
                        if (dte != null)
                        {
                            _runspace.SessionStateProxy.PSVariable.Set("dte", dte);
                        }
                    }
                }

                using (_currentPowerShell = PowerShell.Create())
                {
                    _currentPowerShell.Runspace = _runspace;
                    _currentPowerShell.AddScript(commandLine);

                    _currentPowerShell.AddCommand("out-default");
                    _currentPowerShell.Commands.Commands[0].MergeMyResults(PipelineResultTypes.Error, PipelineResultTypes.Output);

                    var objects = new PSDataCollection<PSObject>();
                    objects.DataAdded += objects_DataAdded;

                    _currentPowerShell.Invoke(null, objects);
                    error = _currentPowerShell.HadErrors;
                }

                return !error;
            }
            catch (Exception ex)
            {
                ServiceCommon.Log("Terminating error,  Exception: {0}", ex);
                OnTerminatingException(ex);
                return false;
            }
            finally
            {
                DebuggerFinished();
            }
        }

        /// <summary>
        /// Stop the current executiong
        /// </summary>
        public void Stop()
        {
            if (_currentPowerShell != null)
            {
                _currentPowerShell.Stop();
            }
        }

        /// <summary>
        /// Get all local scoped variables for client
        /// </summary>
        /// <returns>Collection of variable to client</returns>
        public Collection<Variable> GetScopedVariable()
        {
            Collection<Variable>  variables = new Collection<Variable>();

            foreach (var psobj in _varaiables)
            {
                PSVariable psVar = null;
                if (_runspace.ConnectionInfo == null)
                {
                    psVar = psobj.BaseObject as PSVariable;
                    variables.Add(new Variable(psVar));
                }
                else
                {
                    dynamic dyVar = (dynamic)psobj;

                    if (dyVar.Value == null)
                    {
                        variables.Add(new Variable(dyVar.Name, string.Empty, string.Empty, false, false));
                    }
                    else
                    {
                        if (dyVar.Value is PSObject)
                        {
                            if (((PSObject)dyVar.Value).BaseObject is string)
                            {
                                psVar = new PSVariable(
                                    (string)dyVar.Name,
                                    (PSObject)dyVar.Value,
                                    ScopedItemOptions.None);
                            }
                            else
                            {
                                psVar = new PSVariable(
                                    (string)dyVar.Name,
                                    ((PSObject)dyVar.Value).BaseObject,
                                    ScopedItemOptions.None);
                            }

                            variables.Add(new Variable(psVar));
                        }
                        else
                        {
                            psVar = new PSVariable(
                                (string)dyVar.Name,
                                dyVar.Value.ToString(),
                                ScopedItemOptions.None);
                            variables.Add(new Variable(psVar.Name, psVar.Value.ToString(), dyVar.Value.GetType().ToString(), false, false));
                        }
                    }
                }

                if (psVar != null)
                {
                    _localVariables.Add(psVar);
                }
            }

            return variables;
        }


        /// <summary>
        /// Expand IEnumerable to retrieve all elements
        /// </summary>
        /// <param name="varName">IEnumerable object name</param>
        /// <returns>Collection of variable to client</returns>
        public Collection<Variable> GetExpandedIEnumerableVariable(string varName)
        {
            ServiceCommon.Log("Client tries to watch an IEnumerable variable, dump its content ...");

            Collection<Variable> expandedVariable = new Collection<Variable>();

            object psVariable = RetrieveVariable(varName);

            if (psVariable != null && psVariable is IEnumerable)
            {
                int i = 0;
                foreach (var item in (IEnumerable)psVariable)
                {
                    object obj;
                    var psObj = item as PSObject;
                    if (psObj != null && !(psObj.BaseObject is string))
                    {
                        obj = psObj.BaseObject;
                    }
                    else
                    {
                        obj = item;
                    }

                    expandedVariable.Add(new Variable(String.Format("[{0}]", i), obj.ToString(), obj.GetType().ToString(), obj is IEnumerable, obj is PSObject));

                    if (!obj.GetType().IsPrimitive)
                    {
                        string key = string.Format("{0}\\{1}", varName, String.Format("[{0}]", i));
                        if (!_propVariables.ContainsKey(key))
                        {
                            _propVariables.Add(key, obj);
                        }
                    }

                    i++;
                }
            }

            return expandedVariable;
        }

        /// <summary>
        /// Expand object to retrieve all properties
        /// </summary>
        /// <param name="varName">Object name</param>
        /// <returns>Collection of variable to client</returns>
        public Collection<Variable> GetObjectVariable(string varName)
        {
            ServiceCommon.Log("Client tries to watch an object variable, dump its content ...");

            Collection<Variable> expandedVariable = new Collection<Variable>();

            object psVariable = RetrieveVariable(varName);

            if (psVariable != null && !(psVariable is IEnumerable) && !(psVariable is PSObject))
            {
                var props = psVariable.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance);
                
                foreach (var propertyInfo in props)
                {
                    object val = propertyInfo.GetValue(psVariable, null);
                    expandedVariable.Add(new Variable(propertyInfo.Name, val.ToString(), val.GetType().ToString(), val is IEnumerable, val is PSObject));

                    if (!val.GetType().IsPrimitive)
                    {
                        string key = string.Format("{0}\\{1}", varName, propertyInfo.Name);
                        if (!_propVariables.ContainsKey(key))
                            _propVariables.Add(key, val);
                    }
                }
            }

            return expandedVariable;
        }

        /// <summary>
        /// Expand PSObject to retrieve all its properties
        /// </summary>
        /// <param name="varName">PSObject name</param>
        /// <returns>Collection of variable to client</returns>
        public Collection<Variable> GetPSObjectVariable(string varName)
        {
            ServiceCommon.Log("Client tries to watch an PSObject variable, dump its content ...");

            Collection<Variable> propsVariable = new Collection<Variable>();

            object psVariable = RetrieveVariable(varName);

            if (psVariable != null && psVariable is PSObject)
            {
                foreach (var prop in ((PSObject)psVariable).Properties)
                {
                    if (propsVariable.Any(m => m.VarName == prop.Name))
                    {
                        continue;
                    }

                    object val;
                    try
                    {
                        val = prop.Value;
                        var psObj = val as PSObject;
                        if (psObj != null && !(psObj.BaseObject is string))
                        {
                            val = psObj.BaseObject;
                        }
                    }
                    catch
                    {
                        val = "Failed to evaluate value.";
                    }

                    propsVariable.Add(new Variable(prop.Name, val.ToString(), val.GetType().ToString(), val is IEnumerable, val is PSObject));

                    if (!val.GetType().IsPrimitive)
                    {
                        string key = string.Format("{0}\\{1}", varName, prop.Name);
                        if (!_propVariables.ContainsKey(key))
                            _propVariables.Add(key, val);
                    }
                }
            }

            return propsVariable;
        }

        /// <summary>
        /// Respond client request for callstack frames of current execution context
        /// </summary>
        /// <returns>Collection of callstack to client</returns>
        public IEnumerable<CallStack> GetCallStack()
        {
            ServiceCommon.Log("Obtaining the context for wcf callback");
            List<CallStack> callStackFrames = new List<CallStack>();

            foreach (var psobj in _callstack)
            {
                if (_runspace.ConnectionInfo == null)
                {
                    var frame = psobj.BaseObject as CallStackFrame;
                    if (frame != null)
                    {
                        callStackFrames.Add(new CallStack(frame.ScriptName, frame.FunctionName, frame.ScriptLineNumber));
                    }
                }
                else
                {
                    dynamic psFrame = (dynamic)psobj;

                    callStackFrames.Add(
                        new CallStack(
                            psFrame.ScriptName == null ? string.Empty : _mapRemoteToLocal[(string)psFrame.ScriptName.ToString()],
                            (string)psFrame.FunctionName.ToString(), 
                            (int)psFrame.ScriptLineNumber));
                }
            }

            return callStackFrames;
        }

        /// <summary>
        /// Get prompt string
        /// </summary>
        /// <returns>Prompt string</returns>
        public string GetPrompt()
        {
            using (_currentPowerShell = PowerShell.Create())
            {
                _currentPowerShell.Runspace = _runspace;
                _currentPowerShell.AddCommand("prompt");

                string prompt = _currentPowerShell.Invoke<string>().FirstOrDefault();
                if (_runspace.ConnectionInfo != null)
                {
                    prompt = string.Format("[{0}] {1}", _runspace.ConnectionInfo.ComputerName, prompt);
                }

                return prompt;
            }
        }

        #endregion
    }
}
