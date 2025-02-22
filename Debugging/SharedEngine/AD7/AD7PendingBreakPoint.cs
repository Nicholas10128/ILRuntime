﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using System.IO;
using Microsoft.VisualStudio.Debugger.Interop;
using System.Runtime.InteropServices;

using ILRuntime.Runtime.Debugger.Protocol;
namespace ILRuntimeDebugEngine.AD7
{
    class AD7PendingBreakPoint : IDebugPendingBreakpoint2
    {
        private readonly AD7Engine _engine;
        private readonly IDebugBreakpointRequest2 _pBPRequest;
        private BP_REQUEST_INFO _bpRequestInfo;
        private AD7BoundBreakpoint _boundBreakpoint;
        private AD7ErrorBreakpoint _errorBreakpoint;
        CSBindBreakpoint bindRequest;
        public int StartLine { get; private set; }
        public int StartColumn { get; private set; }
        public int EndLine { get; private set; }
        public int EndColumn { get; private set; }
        public string DocumentName { get; private set; }
        public enum_BP_STATE State { get; private set; }
        public bool IsBound { get { return _boundBreakpoint != null; } }
        public string ConditionExpression { get { return _bpRequestInfo.bpCondition.bstrCondition; } }

        public AD7PendingBreakPoint(AD7Engine engine, IDebugBreakpointRequest2 pBPRequest)
        {
            var requestInfo = new BP_REQUEST_INFO[1];
            pBPRequest.GetRequestInfo(enum_BPREQI_FIELDS.BPREQI_ALLFIELDS, requestInfo);
            _bpRequestInfo = requestInfo[0];
            _pBPRequest = pBPRequest;
            _engine = engine;

            //Enabled = true;

            var docPosition =
                (IDebugDocumentPosition2)Marshal.GetObjectForIUnknown(_bpRequestInfo.bpLocation.unionmember2);

            string documentName;
            docPosition.GetFileName(out documentName);
            var startPosition = new TEXT_POSITION[1];
            var endPosition = new TEXT_POSITION[1];
            docPosition.GetRange(startPosition, endPosition);

            DocumentName = documentName;
            StartLine = (int)startPosition[0].dwLine;
            StartColumn = (int)startPosition[0].dwColumn;

            EndLine = (int)endPosition[0].dwLine;
            EndColumn = (int)endPosition[0].dwColumn;
        }

        public int Bind()
        {
            TryBind();
            return Constants.S_OK;
        }

        public int CanBind(out IEnumDebugErrorBreakpoints2 ppErrorEnum)
        {
            throw new NotImplementedException();
        }

        public int Delete()
        {
            if (_engine != null && _engine.DebuggedProcess != null)
                _engine.DebuggedProcess.SendDeleteBreakpoint(GetHashCode());
            State = enum_BP_STATE.BPS_DELETED;
            return Constants.S_OK;
        }

        public int Enable(int fEnable)
        {
            State = fEnable != 0 ? enum_BP_STATE.BPS_ENABLED : enum_BP_STATE.BPS_DISABLED;
            if (bindRequest != null)
                _engine.DebuggedProcess.SendSetBreakpointEnabled(GetHashCode(), State == enum_BP_STATE.BPS_ENABLED);
            return Constants.S_OK;
        }

        public int EnumBoundBreakpoints(out IEnumDebugBoundBreakpoints2 ppEnum)
        {
            if (_boundBreakpoint != null)
                ppEnum = new AD7BoundBreakpointsEnum(new[] { _boundBreakpoint });
            else
                ppEnum = null;
            return Constants.S_OK;
        }

        public int EnumErrorBreakpoints(enum_BP_ERROR_TYPE bpErrorType, out IEnumDebugErrorBreakpoints2 ppEnum)
        {
            if (_errorBreakpoint != null)
                ppEnum = new AD7ErrorBreakpointsEnum(new[] { _errorBreakpoint });
            else
                ppEnum = null;
            return Constants.S_OK;
        }

        public int GetBreakpointRequest(out IDebugBreakpointRequest2 ppBPRequest)
        {
            ppBPRequest = _pBPRequest;
            return Constants.S_OK;
        }

        public int GetState(PENDING_BP_STATE_INFO[] pState)
        {
            pState[0].state = (enum_PENDING_BP_STATE)State;
            return Constants.S_OK;
        }

        public int SetCondition(BP_CONDITION bpCondition)
        {
            _bpRequestInfo.bpCondition = bpCondition;
            _engine.DebuggedProcess.SendSetBreakpointCondition(this.GetHashCode(), bpCondition.styleCondition, bpCondition.bstrCondition);
            return Constants.S_OK;
        }

        public int SetPassCount(BP_PASSCOUNT bpPassCount)
        {
            throw new NotImplementedException();
        }

        public int Virtualize(int fVirtualize)
        {
            return Constants.S_OK;
        }

        internal bool TryBind()
        {
            try
            {
                if (bindRequest == null)
                {
                    using (var stream = File.OpenRead(DocumentName))
                    {
                        SyntaxTree syntaxTree = CSharpSyntaxTree.ParseText(SourceText.From(stream), path: DocumentName);
                        TextLine textLine = syntaxTree.GetText().Lines[StartLine];
                        Location location = syntaxTree.GetLocation(textLine.Span);
                        SyntaxTree sourceTree = location.SourceTree;
                        SyntaxNode node = location.SourceTree.GetRoot().FindNode(location.SourceSpan, true, true);

                        bool isLambda = GetParentMethod<LambdaExpressionSyntax>(node.Parent) != null;
                        BaseMethodDeclarationSyntax method = GetParentMethod<MethodDeclarationSyntax>(node.Parent);
                        string methodName = null;
                        if (method != null)
                            methodName = ((MethodDeclarationSyntax)method).Identifier.Text;
                        else
                        {
                            method = GetParentMethod<ConstructorDeclarationSyntax>(node.Parent);
                            if (method != null)
                            {
                                bool isStatic = false;
                                foreach (var i in method.Modifiers)
                                {
                                    if (i.Text == "static")
                                        isStatic = true;
                                }
                                if (isStatic)
                                    methodName = ".cctor";
                                else
                                    methodName = ".ctor";
                            }
                        }

                        string className = GetClassName(method);

                        //var ns = GetParentMethod<NamespaceDeclarationSyntax>(method);
                        //string nsname = ns != null ? ns.Name.ToString() : null;

                        //string name = ns != null ? string.Format("{0}.{1}", nsname, className) : className;
                        var nameSpaceStack = new Stack<string>();
                        var usingSyntaxList = new List<UsingDirectiveSyntax>(syntaxTree.GetCompilationUnitRoot().Usings);
                        GetCurrentNameSpaceDeclaration(node.Parent, nameSpaceStack, usingSyntaxList);

                        bindRequest = new CSBindBreakpoint();
                        bindRequest.BreakpointHashCode = this.GetHashCode();
                        bindRequest.IsLambda = isLambda;
                        bindRequest.NamespaceName = string.Join(".", nameSpaceStack);
                        bindRequest.TypeName = className;
                        bindRequest.MethodName = methodName;
                        bindRequest.StartLine = StartLine;
                        bindRequest.EndLine = EndLine;
                        bindRequest.Enabled = State == enum_BP_STATE.BPS_ENABLED;
                        bindRequest.Condition = new BreakpointCondition();
                        bindRequest.Condition.Style = (BreakpointConditionStyle)_bpRequestInfo.bpCondition.styleCondition;
                        bindRequest.Condition.Expression = _bpRequestInfo.bpCondition.bstrCondition;
                        bindRequest.UsingInfos = usingSyntaxList.Select(n => new UsingInfo
                        {
                            Alias = n.Alias != null ? n.Alias.Name.ToString() : "",
                            Name = n.Name.ToString(),
                        }).ToArray();
                    }
                }

                _engine.DebuggedProcess.SendBindBreakpoint(bindRequest);
                return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(ex.ToString());
            }
            return false;
        }

        string GetClassName(BaseMethodDeclarationSyntax method)
        {
            ClassDeclarationSyntax cur = GetParentMethod<ClassDeclarationSyntax>(method);
            string clsName = null;
            while (cur != null)
            {
                if (clsName == null)
                    clsName = cur.Identifier.Text;
                else
                    clsName = string.Format("{0}/{1}", cur.Identifier.Text, clsName);
                cur = GetParentMethod<ClassDeclarationSyntax>(cur.Parent);
            }

            return clsName;
        }
        private T GetParentMethod<T>(SyntaxNode node) where T : SyntaxNode
        {
            if (node == null)
                return null;

            if (node is T)
                return node as T;
            return GetParentMethod<T>(node.Parent);
        }

        private void GetCurrentNameSpaceDeclaration(SyntaxNode node, Stack<string> namespaceList, List<UsingDirectiveSyntax> usingSyntaxList)
        {
            if (node == null)
                return;

            if (node is NamespaceDeclarationSyntax)
            {
                var namespaceDeclarationSyntax = node as NamespaceDeclarationSyntax;
                namespaceList.Push(namespaceDeclarationSyntax.Name.ToString());
                usingSyntaxList.AddRange(namespaceDeclarationSyntax.Usings);
            }
            GetCurrentNameSpaceDeclaration(node.Parent, namespaceList, usingSyntaxList);
        }

        public void Bound(BindBreakpointResults result)
        {
            if (result == BindBreakpointResults.OK)
            {
                _boundBreakpoint = new AD7BoundBreakpoint(_engine, this);
                _errorBreakpoint = null;
                _engine.Callback.BoundBreakpoint(this);
            }
            else
            {
                _errorBreakpoint = new AD7ErrorBreakpoint(_engine, this);
                _boundBreakpoint = null;
                _engine.Callback.ErrorBreakpoint(_errorBreakpoint);
            }
        }
    }
}