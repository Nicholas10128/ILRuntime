﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using ILRuntime.Runtime.Debugger;
using ILRuntime.Runtime.Debugger.Protocol;
using Microsoft.VisualStudio.Debugger.Interop;

namespace ILRuntimeDebugEngine.AD7
{
    class DebuggedProcess
    {
        System.IO.MemoryStream sendStream = new System.IO.MemoryStream(64 * 1024);
        System.IO.BinaryWriter bw;
        bool closed;
        DebugSocket socket;
        AD7Engine engine;
        bool rpcStarted = false;
        bool rpcCompleted = false;
        object rpcResult;
        Dictionary<int, AD7PendingBreakPoint> breakpoints = new Dictionary<int, AD7PendingBreakPoint>();
        Dictionary<int, AD7Thread> threads = new Dictionary<int, AD7Thread>();
        public Dictionary<int, AD7Thread> Threads { get { return threads; } }

        public Action OnDisconnected { get; set; }

        public bool Connected { get; set; }

        public bool ConnectFailed { get; set; }

        public bool Connecting { get; set; }

        public int RemoteDebugVersion { get; set; }

        private readonly string BreakpointErrorMsg =
            "不能设置下面的断点:" +
            Environment.NewLine +
            "于{0}, 行{1}字符{2}, 条件是\"{3}\"为true" +
            Environment.NewLine +
            "断点的条件未能执行。错误是\"{4}\"。";

        public DebuggedProcess(AD7Engine engine, string host, int port)
        {
            this.engine = engine;
            bw = new System.IO.BinaryWriter(sendStream);
            socket = new DebugSocket();
            socket.OnConnect = OnConnected;
            socket.OnConnectFailed = OnConnectFailed;
            socket.OnReciveMessage = OnReceiveMessage;
            socket.OnClose = OnClose;
            socket.Connect(host, port);
            Connecting = true;
        }

        void OnConnected()
        {
            Connected = true;
            Connecting = false;
        }

        void OnConnectFailed()
        {
            ConnectFailed = true;
            Connecting = false;
        }

        public void Close()
        {
            socket.OnClose = null;
            socket.Close();
        }

        void OnClose()
        {
            closed = true;
            if (OnDisconnected != null)
                OnDisconnected();
        }

        bool waitingAttach;
        public bool CheckDebugServerVersion()
        {
            socket.Send(DebugMessageType.CSAttach, new byte[0], 0);
            waitingAttach = true;
            DateTime now = DateTime.Now;
            while (waitingAttach)
            {
                System.Threading.Thread.Sleep(1);
                if ((DateTime.Now - now).TotalSeconds >= 30 || !Connected)
                {
                    return false;
                }
            }

            return RemoteDebugVersion == DebuggerServer.Version;
        }

        public T AwaitRPCRequest<T>(out bool aborted, int timeout = 0)
        {
            aborted = false;
            if (!rpcStarted)
            {
                rpcCompleted = false;
                rpcResult = null;
                rpcStarted = true;
                var rpcStartTime = DateTime.Now;
                while (!rpcCompleted)
                {
                    if (timeout > 0)
                    {
                        if ((DateTime.Now - rpcStartTime).TotalMilliseconds > timeout)
                        {
                            rpcCompleted = true;
                            rpcResult = null;
                            rpcStarted = false;
                            aborted = true;
                            break;
                        }
                    }
                    if (closed)
                    {
                        rpcCompleted = true;
                        rpcResult = null;
                        rpcStarted = false;
                        aborted = true;
                        break;
                    }
                    System.Threading.Thread.Sleep(10);
                }

                return (T)rpcResult;
            }
            else
                throw new NotSupportedException();
        }

        void CompleteRPCRequest(object obj)
        {
            if (rpcStarted)
            {
                rpcResult = obj;
                rpcCompleted = true;
                rpcStarted = false;
            }
        }

        void OnReceiveMessage(DebugMessageType type, byte[] buffer)
        {
            using (System.IO.MemoryStream ms = new System.IO.MemoryStream(buffer))
            {
                using (System.IO.BinaryReader br = new System.IO.BinaryReader(ms))
                {
                    switch (type)
                    {
                        case DebugMessageType.SCAttachResult:
                            {
                                SCAttachResult result = new SCAttachResult();
                                result.Result = (AttachResults)br.ReadByte();
                                result.DebugServerVersion = br.ReadInt32();
                                RemoteDebugVersion = result.DebugServerVersion;
                                waitingAttach = false;
                            }
                            break;
                        case DebugMessageType.SCBindBreakpointResult:
                            {
                                SCBindBreakpointResult msg = new SCBindBreakpointResult();
                                msg.BreakpointHashCode = br.ReadInt32();
                                msg.Result = (BindBreakpointResults)br.ReadByte();
                                OnReceivSendSCBindBreakpointResult(msg);
                            }
                            break;
                        case DebugMessageType.SCBreakpointHit:
                            {
                                SCBreakpointHit msg = new SCBreakpointHit();
                                msg.BreakpointHashCode = br.ReadInt32();
                                msg.ThreadHashCode = br.ReadInt32();
                                msg.StackFrame = ReadStackFrames(br);
                                OnReceiveSCBreakpointHit(msg, br.ReadString());
                            }
                            break;

                        case DebugMessageType.SCStepComplete:
                            {
                                SCStepComplete msg = new SCStepComplete();
                                msg.ThreadHashCode = br.ReadInt32();
                                msg.StackFrame = ReadStackFrames(br);
                                OnReceiveSCStepComplete(msg);
                            }
                            break;
                        case DebugMessageType.SCThreadStarted:
                            {
                                SCThreadStarted msg = new SCThreadStarted();
                                msg.ThreadHashCode = br.ReadInt32();
                                OnReceiveSCThreadStarted(msg);
                            }
                            break;
                        case DebugMessageType.SCThreadEnded:
                            {
                                SCThreadEnded msg = new SCThreadEnded();
                                msg.ThreadHashCode = br.ReadInt32();
                                OnReceiveSCThreadEnded(msg);
                            }
                            break;
                        case DebugMessageType.SCModuleLoaded:
                            {
                                SCModuleLoaded msg = new SCModuleLoaded();
                                msg.ModuleName = br.ReadString();

                                OnReceiveSCModuleLoaded(msg);
                            }
                            break;
                        case DebugMessageType.SCResolveVariableResult:
                            {
                                CompleteRPCRequest(ReadVariableInfo(br));
                            }
                            break;
                        case DebugMessageType.SCEnumChildrenResult:
                            {
                                int cnt = br.ReadInt32();
                                VariableInfo[] res = new VariableInfo[cnt];
                                for (int i = 0; i < cnt; i++)
                                {
                                    res[i] = ReadVariableInfo(br);
                                }
                                CompleteRPCRequest(res);
                            }
                            break;
                    }
                }
            }
        }

        KeyValuePair<int, StackFrameInfo[]>[] ReadStackFrames(System.IO.BinaryReader br)
        {
            int len = br.ReadInt32();
            KeyValuePair<int, StackFrameInfo[]>[] res = new KeyValuePair<int, StackFrameInfo[]>[len];
            for (int i = 0; i < len; i++)
            {
                int key = br.ReadInt32();
                int cnt = br.ReadInt32();
                StackFrameInfo[] arr = new StackFrameInfo[cnt + 1];
                for (int j = 0; j < cnt; j++)
                {
                    StackFrameInfo info = new StackFrameInfo();
                    info.MethodName = br.ReadString();
                    info.DocumentName = br.ReadString();
                    info.StartLine = br.ReadInt32();
                    info.StartColumn = br.ReadInt32();
                    info.EndLine = br.ReadInt32();
                    info.EndColumn = br.ReadInt32();
                    int vcnt = br.ReadInt32();
                    info.LocalVariables = new VariableInfo[vcnt];
                    for (int k = 0; k < vcnt; k++)
                    {
                        info.LocalVariables[k] = ReadVariableInfo(br);
                    }
                    arr[j] = info;
                }
                arr[cnt] = new StackFrameInfo()
                {
                    MethodName = "Transition to Native methods"
                };
                res[i] = new KeyValuePair<int, StackFrameInfo[]>(key, arr);
            }

            return res;
        }

        VariableInfo ReadVariableInfo(System.IO.BinaryReader br)
        {
            VariableInfo vinfo = new VariableInfo();
            vinfo.Address = br.ReadInt64();
            vinfo.Type = (VariableTypes)br.ReadByte();
            vinfo.Offset = br.ReadInt32();
            vinfo.Name = br.ReadString();
            vinfo.Value = br.ReadString();
            vinfo.ValueType = (ValueTypes)br.ReadByte();
            vinfo.TypeName = br.ReadString();
            vinfo.Expandable = br.ReadBoolean();
            vinfo.IsPrivate = br.ReadBoolean();
            vinfo.IsProtected = br.ReadBoolean();

            return vinfo;
        }
        public void AddPendingBreakpoint(AD7PendingBreakPoint bp)
        {
            breakpoints[bp.GetHashCode()] = bp;
        }

        public void SendBindBreakpoint(CSBindBreakpoint msg)
        {
            sendStream.Position = 0;
            bw.Write(msg.BreakpointHashCode);
            bw.Write(msg.IsLambda);
            bw.Write(msg.NamespaceName);
            bw.Write(msg.TypeName);
            bw.Write(msg.MethodName);
            bw.Write(msg.StartLine);
            bw.Write(msg.EndLine);
            bw.Write(msg.Enabled);
            bw.Write((byte)msg.Condition.Style);
            if (msg.Condition.Style != BreakpointConditionStyle.None)
                bw.Write(msg.Condition.Expression);
            bw.Write(msg.UsingInfos.Length);
            foreach (var usingInfo in msg.UsingInfos)
            {
                bw.Write(usingInfo.Alias);
                bw.Write(usingInfo.Name);
            }
            socket.Send(DebugMessageType.CSBindBreakpoint, sendStream.GetBuffer(), (int)sendStream.Position);
        }

        public void SendSetBreakpointCondition(int bpHash, enum_BP_COND_STYLE style, string expression)
        {
            sendStream.Position = 0;
            bw.Write(bpHash);
            bw.Write((byte)style);
            if (style != enum_BP_COND_STYLE.BP_COND_NONE)
                bw.Write(expression);
            socket.Send(DebugMessageType.CSSetBreakpointCondition, sendStream.GetBuffer(), (int)sendStream.Position);
        }

        public void SendSetBreakpointEnabled(int bpHash, bool enabled)
        {
            sendStream.Position = 0;
            bw.Write(bpHash);
            bw.Write(enabled);
            socket.Send(DebugMessageType.CSSetBreakpointEnabled, sendStream.GetBuffer(), (int)sendStream.Position);
        }

        public void SendDeleteBreakpoint(int bpHash)
        {
            sendStream.Position = 0;
            bw.Write(bpHash);
            socket.Send(DebugMessageType.CSDeleteBreakpoint, sendStream.GetBuffer(), (int)sendStream.Position);
        }

        public void SendExecute(int threadHash)
        {
            sendStream.Position = 0;
            bw.Write(threadHash);
            socket.Send(DebugMessageType.CSExecute, sendStream.GetBuffer(), (int)sendStream.Position);
        }

        public void SendStep(int threadHash, StepTypes type)
        {
            sendStream.Position = 0;
            bw.Write(threadHash);
            bw.Write((byte)type);
            socket.Send(DebugMessageType.CSStep, sendStream.GetBuffer(), (int)sendStream.Position);
        }

        void OnReceivSendSCBindBreakpointResult(SCBindBreakpointResult msg)
        {
            AD7PendingBreakPoint bp;
            if (breakpoints.TryGetValue(msg.BreakpointHashCode, out bp))
            {
                bp.Bound(msg.Result);
            }
        }

        void OnReceiveSCModuleLoaded(SCModuleLoaded msg)
        {
            engine.Callback.ModuleLoaded(new AD7Module(msg.ModuleName));

            foreach (var i in breakpoints)
            {
                if (!i.Value.IsBound)
                {
                    i.Value.TryBind();
                }
            }
        }

        //VariableInfo resolved;
        public VariableInfo ResolveVariable(VariableReference parent, string name, int threadId, int frameId, uint dwTimeout)
        {
            CSResolveVariable msg = new CSResolveVariable();
            msg.ThreadHashCode = threadId;
            msg.FrameIndex = frameId;
            msg.Variable = VariableReference.GetMember(name, parent);
            SendResolveVariable(msg);

            bool aborted;
            var res = AwaitRPCRequest<VariableInfo>(out aborted, (int)dwTimeout);
            if (aborted)
                return VariableInfo.RequestTimeout;
            return res;
        }

        void SendResolveVariable(CSResolveVariable msg)
        {
            sendStream.Position = 0;
            bw.Write(msg.ThreadHashCode);
            bw.Write(msg.FrameIndex);
            WriteVariableReference(msg.Variable);
            socket.Send(DebugMessageType.CSResolveVariable, sendStream.GetBuffer(), (int)sendStream.Position);
        }

        public VariableInfo ResolveIndexAccess(VariableReference body, VariableReference idx, int threadId, int frameId, uint dwTimeout)
        {
            CSResolveIndexer msg = new CSResolveIndexer();
            msg.Body = body;
            msg.Index = idx;
            msg.ThreadHashCode = threadId;
            msg.FrameIndex = frameId;
            SendResolveIndexAccess(msg);

            bool aborted;
            var res = AwaitRPCRequest<VariableInfo>(out aborted, (int)dwTimeout);
            if (aborted)
                return VariableInfo.RequestTimeout;
            return res;
        }

        void SendResolveIndexAccess(CSResolveIndexer msg)
        {
            sendStream.Position = 0;
            bw.Write(msg.ThreadHashCode);
            bw.Write(msg.FrameIndex);
            WriteVariableReference(msg.Body);
            WriteVariableReference(msg.Index);
            socket.Send(DebugMessageType.CSResolveIndexAccess, sendStream.GetBuffer(), (int)sendStream.Position);
        }

        public VariableInfo[] EnumChildren(VariableReference parent, int threadId, int frameId, uint dwTimeout)
        {
            CSEnumChildren msg = new CSEnumChildren();
            msg.ThreadHashCode = threadId;
            msg.FrameIndex = frameId;
            msg.Parent = parent;
            SendEnumChildren(msg);

            bool aborted;
            var res = AwaitRPCRequest<VariableInfo[]>(out aborted, (int)dwTimeout);
            if (aborted)
                return new VariableInfo[] { VariableInfo.RequestTimeout };
            return res;
        }

        void SendEnumChildren(CSEnumChildren msg)
        {
            sendStream.Position = 0;
            bw.Write(msg.ThreadHashCode);
            bw.Write(msg.FrameIndex);
            WriteVariableReference(msg.Parent);
            socket.Send(DebugMessageType.CSEnumChildren, sendStream.GetBuffer(), (int)sendStream.Position);
        }

        void WriteVariableReference(VariableReference reference)
        {
            bw.Write(reference != null);
            if (reference != null)
            {
                bw.Write(reference.Address);
                bw.Write((byte)reference.Type);
                bw.Write(reference.Offset);
                bw.Write(reference.Name);
                WriteVariableReference(reference.Parent);
                if (reference.Parameters != null)
                {
                    bw.Write(reference.Parameters.Length);
                    foreach (var i in reference.Parameters)
                    {
                        WriteVariableReference(i);
                    }
                }
                else
                    bw.Write(0);
            }
        }

        void OnReceiveSCBreakpointHit(SCBreakpointHit msg, string error)
        {
            AD7PendingBreakPoint bp;
            AD7Thread t, bpThread = null;
            if (breakpoints.TryGetValue(msg.BreakpointHashCode, out bp))
            {
                foreach (var i in msg.StackFrame)
                {
                    if (threads.TryGetValue(i.Key, out t))
                    {
                        t.StackFrames = i.Value;
                        if (i.Key == msg.ThreadHashCode)
                            bpThread = t;
                    }
                }
                if (bpThread != null)
                {
                    engine.Callback.BreakpointHit(bp, bpThread);
                    if (!string.IsNullOrWhiteSpace(error))
                    {
                        var errorMsg = string.Format(BreakpointErrorMsg, System.IO.Path.GetFileName(bp.DocumentName), bp.StartLine + 1, bp.StartColumn, bp.ConditionExpression, error);
                        if (AD7Engine.ShowErrorMessageBoxAction != null)
                            AD7Engine.ShowErrorMessageBoxAction("ILRuntime Debugger", errorMsg);
                        else
                            MessageBox.Show(errorMsg, "ILRuntime Debugger", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }
                }
            }
        }

        void OnReceiveSCStepComplete(SCStepComplete msg)
        {
            AD7Thread t, bpThread = null;

            foreach (var i in msg.StackFrame)
            {
                if (threads.TryGetValue(i.Key, out t))
                {
                    t.StackFrames = i.Value;
                    if (i.Key == msg.ThreadHashCode)
                        bpThread = t;
                }
            }
            if (bpThread != null)
                engine.Callback.StepCompleted(bpThread);

        }
        void OnReceiveSCThreadStarted(SCThreadStarted msg)
        {
            AD7Thread t = new AD7Thread(engine, msg.ThreadHashCode);
            threads[msg.ThreadHashCode] = t;
            engine.Callback.ThreadStarted(t);
        }

        void OnReceiveSCThreadEnded(SCThreadEnded msg)
        {
            AD7Thread t;
            if (threads.TryGetValue(msg.ThreadHashCode, out t))
            {
                engine.Callback.ThreadEnded(t);
                threads.Remove(msg.ThreadHashCode);
            }
        }
    }
}