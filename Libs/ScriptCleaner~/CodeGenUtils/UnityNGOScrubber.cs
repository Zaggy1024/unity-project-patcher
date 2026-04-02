using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Nomnom.CodeGenUtils {
    public static class UnityNGOScrubber {
        private static SyntaxNode Scrub__rpcCalls(SyntaxNode root) {
            var methodsToRemove = root.DescendantNodes()
                .OfType<MethodDeclarationSyntax>()
                .Where(m => m.Identifier.Text.StartsWith("__getTypeName") || m.Identifier.Text.StartsWith("__initializeVariables") ||
                            m.Identifier.Text.StartsWith("__initializeRpcs") || m.Identifier.Text.StartsWith("InitializeRPCS_") ||
                            m.Identifier.Text.StartsWith("__rpc_handler_"));
            var newRoot = root.RemoveNodes(methodsToRemove, SyntaxRemoveOptions.KeepNoTrivia)!;
            return newRoot;
        }

        private static bool IsRpcAttribute(AttributeSyntax attribute)
        {
            var attributeName = attribute.Name.ToString();
            return attributeName == "ServerRpc"
                || attributeName == "ClientRpc"
                || attributeName == "Rpc"
                || attributeName.EndsWith(".ServerRpc")
                || attributeName.EndsWith(".ClientRpc")
                || attributeName.EndsWith(".Rpc")
                || attributeName.EndsWith("ServerRpcAttribute")
                || attributeName.EndsWith("ClientRpcAttribute")
                || attributeName.EndsWith("RpcAttribute");
        }

        public static void ScrubDecompiledScript(string[] files, bool outputCopy, Action<string> log)
        {
            foreach (var file in files)
            {
                try
                {
                    // delete the copy file if it exists
                    var copyFile = file.Replace(".cs", ".copy.cs");
                    if (File.Exists(copyFile))
                    {
                        File.Delete(copyFile);
                    }

                    if (!File.Exists(file))
                    {
                        // log($"[error] File \"{file}\" does not exist");
                        continue;
                    }

                    var fileName = Path.GetFileNameWithoutExtension(file);
                    if (fileName == "UnitySourceGeneratedAssemblyMonoScriptTypes_v1")
                    {
                        File.Delete(file);
                        continue;
                    }

                    var text = File.ReadAllText(file);
                    var tree = CSharpSyntaxTree.ParseText(text);
                    var root = tree.GetRoot();
                    root = Scrub__rpcCalls(root);

                    var methods = root.DescendantNodes().OfType<MemberDeclarationSyntax>().ToArray();
                    var methodsToReplace = new List<(MethodDeclarationSyntax, MethodDeclarationSyntax)>();
                    var nodesToRemove = new List<SyntaxNode>();

                    var bannedAttributes = new string[] {
                        "MonoPInvokeCallback"
                    };

                    foreach (var method in methods)
                    {
                        if (method is MethodDeclarationSyntax methodDeclaration)
                        {
                            // log($"[info] - method name: {methodDeclaration.Identifier.Text}");
                            var attributes = methodDeclaration.AttributeLists
                                .SelectMany(x => x.Attributes)
                                .ToArray();

                            if (attributes.Any(x => bannedAttributes.Contains(x.Name.ToString())))
                            {
                                nodesToRemove.AddRange(attributes);
                                nodesToRemove.Add(methodDeclaration);
                                continue;
                            }

                            MethodDeclarationSyntax? newMethod = null;
                            if (attributes.Any(IsRpcAttribute))
                            {
                                newMethod = HandleRpcFunction(methodDeclaration, log);
                            }
                            else
                            {
                                // log("unknown function");
                                continue;
                            }

                            if (newMethod != null)
                            {
                                newMethod = newMethod.NormalizeWhitespace("\t", "\r\n");
                                newMethod = (MethodDeclarationSyntax)new IndentStatements(2).Visit(newMethod);
                                newMethod = newMethod.WithLeadingTrivia(newMethod.GetLeadingTrivia().Prepend(SyntaxFactory.CarriageReturnLineFeed));
                                newMethod = newMethod.WithTrailingTrivia(newMethod.GetTrailingTrivia().Append(SyntaxFactory.CarriageReturnLineFeed));
                                // log($"[info] new method: {newMethod}");
                                methodsToReplace.Add((methodDeclaration, newMethod));
                            }
                        }
                    }

                    // replace old methods with new methods
                    root = root.ReplaceNodes(methodsToReplace.Select(x => x.Item1), (x, y) => methodsToReplace.First(z => z.Item1 == x).Item2);
                    root = root.RemoveNodes(nodesToRemove, SyntaxRemoveOptions.KeepNoTrivia)!;

                    // write the new code back to the file
                    var newCode = root.ToFullString();

                    //todo: remove this from the generic code
                    var startOfRoundClass = root.DescendantNodes().OfType<ClassDeclarationSyntax>().FirstOrDefault(x => x.Identifier.Text == "StartOfRound");
                    if (startOfRoundClass != null)
                    {
                        // log($"[class] {startOfRoundClass.Identifier.Text}");
                        newCode = newCode.Replace(
                            @"voiceChatModule.IsMuted = !IngamePlayerSettings.Instance.playerInput.actions.FindAction(""VoiceButton"").IsPressed() && !GameNetworkManager.Instance.localPlayerController.speakingToWalkieTalkie;",
                            @"// voiceChatModule.IsMuted = !IngamePlayerSettings.Instance.playerInput.actions.FindAction(""VoiceButton"").IsPressed() && !GameNetworkManager.Instance.localPlayerController.speakingToWalkieTalkie;"
                        );

                        newCode = newCode.Replace(
                            @"HUDManager.Instance.PTTIcon.enabled = IngamePlayerSettings.Instance.settings.micEnabled && !voiceChatModule.IsMuted;",
                            @"// HUDManager.Instance.PTTIcon.enabled = IngamePlayerSettings.Instance.settings.micEnabled && !voiceChatModule.IsMuted;"
                        );

                        newCode = newCode.Replace(
                            @"voiceChatModule.IsMuted = !IngamePlayerSettings.Instance.settings.micEnabled;",
                            @"// voiceChatModule.IsMuted = !IngamePlayerSettings.Instance.settings.micEnabled;"
                        );

                        newCode = newCode.Replace(
                            @"if (GameNetworkManager.Instance == null || GameNetworkManager.Instance.localPlayerController == null || GameNetworkManager.Instance.localPlayerController.isPlayerDead || voiceChatModule.IsMuted || !voiceChatModule.enabled || voiceChatModule == null)",
                            @"if (GameNetworkManager.Instance == null || GameNetworkManager.Instance.localPlayerController == null || GameNetworkManager.Instance.localPlayerController.isPlayerDead || voiceChatModule == null)"
                        );

                        newCode = newCode.Replace(
                            @"allPlayerScripts[i].gameObject.GetComponentInChildren<NfgoPlayer>().VoiceChatTrackingStart();",
                            @"// allPlayerScripts[i].gameObject.GetComponentInChildren<NfgoPlayer>().VoiceChatTrackingStart();"
                        );

                        newCode = newCode.Replace(
                            @"playerControllerB.gameObject.GetComponentInChildren<NfgoPlayer>().VoiceChatTrackingStart();",
                            @"// playerControllerB.gameObject.GetComponentInChildren<NfgoPlayer>().VoiceChatTrackingStart();"
                        );
                    }

                    // write to copy file
                    File.WriteAllText(outputCopy ? copyFile : file, newCode);
                    // File.WriteAllText(file, newCode);
                }
                catch (Exception e)
                {
                    log($"[error] {e}");
                }
            }
        }

        private static IEnumerable<StatementSyntax> TryUnwrapBlock(StatementSyntax syntax) {
            if (syntax is BlockSyntax block) {
                return block.Statements;
            }
            return new[] { syntax };
        }

        private static BlockSyntax MakeRPCMethodBody(IEnumerable<StatementSyntax> syntax, Action<string> log) {
            var first = syntax.FirstOrDefault();
            if (first != null && first.ToString() == "__rpc_exec_stage = __RpcExecStage.Send;") {
                syntax = syntax.Skip(1);
            }
            return SyntaxFactory.Block(syntax);
        }

        private static BlockSyntax MakeRPCMethodBody(StatementSyntax syntax, Action<string> log) {
            return MakeRPCMethodBody(new[] { syntax }, log);
        }

        private static MethodDeclarationSyntax? HandleRpcFunction(MethodDeclarationSyntax methodDeclaration, Action<string> log) {
            var statements = methodDeclaration.Body?.Statements;
            if (statements is not { } validStatements) {
                log($"[error] has no statements");
                return null;
            }

            /*
             * Example:
             *      NetworkManager networkManager = base.NetworkManager;
             *      if ((object)networkManager != null && networkManager.IsListening)
             *      {
             */
            if (validStatements.Count == 2) {
                var secondStatement = validStatements[1];
                if (secondStatement is not IfStatementSyntax secondStatementIf) {
                    log($"[error] no secondStatementIf");
                    return null;
                }

                var childNodes = secondStatementIf.ChildNodes().ToArray();
                // foreach (var child in childNodes) {
                //     log($"[child] is {child.GetType().FullName}: {child}");
                // }
                if (childNodes.Length > 1) {
                    var secondChildNode = childNodes[1];
                    childNodes = secondChildNode.ChildNodes().ToArray();
                    // foreach (var child in childNodes) {
                    //     log($"- [child] is {child.GetType().FullName}: {child}");
                    // }

                    var nestedNode = childNodes.Length == 1 ? childNodes[0] : childNodes[1];
                    if (nestedNode is not IfStatementSyntax nestedNodeIf) {
                        log($"[error] no nestedNodeIf");
                        return null;
                    }
                
                    // strip this if statement of the prefix info and keep the rest
                    var strippedIfStatement = StripIfStatement(nestedNodeIf, log);
                    if (strippedIfStatement != null) {
                        var newMethod = SyntaxFactory.MethodDeclaration(methodDeclaration.ReturnType, methodDeclaration.Identifier)
                            .WithModifiers(methodDeclaration.Modifiers)
                            .WithParameterList(methodDeclaration.ParameterList)
                            .WithAttributeLists(methodDeclaration.AttributeLists)
                            .WithBody(MakeRPCMethodBody(strippedIfStatement, log));
                        return newMethod;
                    } else {
                        var newMethod = SyntaxFactory.MethodDeclaration(methodDeclaration.ReturnType, methodDeclaration.Identifier)
                            .WithModifiers(methodDeclaration.Modifiers)
                            .WithParameterList(methodDeclaration.ParameterList)
                            .WithAttributeLists(methodDeclaration.AttributeLists)
                            .WithBody(MakeRPCMethodBody(((BlockSyntax)nestedNodeIf.Statement).Statements, log));
                        return newMethod;
                    }
                } else {
                    log($"[error] unexpected childNodes length = {childNodes.Length} for Count==2");
                    return null;
                }
            }
            /*
             * Example:
             *      NetworkManager networkManager = base.NetworkManager;
             *      if ((object)networkManager == null || !networkManager.IsListening)
             *      {
             *          return;
             *      }
             *      if (__rpc_exec_stage != __RpcExecStage.Client && (networkManager.IsServer || networkManager.IsHost))
             *      {
             *          ClientRpcParams clientRpcParams = default(ClientRpcParams);
             *          FastBufferWriter bufferWriter = __beginSendClientRpc(848048148u, clientRpcParams, RpcDelivery.Reliable);
             *          bufferWriter.WriteValueSafe(in setBool, default(FastBufferWriter.ForPrimitives));
             *          bufferWriter.WriteValueSafe(in playSecondaryAudios, default(FastBufferWriter.ForPrimitives));
             *          BytePacker.WriteValueBitPacked(bufferWriter, playerWhoTriggered);
             *          __endSendClientRpc(ref bufferWriter, 848048148u, clientRpcParams, RpcDelivery.Reliable);
             *      }
             *      if (__rpc_exec_stage != __RpcExecStage.Client || (!networkManager.IsClient && !networkManager.IsHost) || GameNetworkManager.Instance.localPlayerController == null || (playerWhoTriggered != -1 && (int)GameNetworkManager.Instance.localPlayerController.playerClientId == playerWhoTriggered))
             */
            else if (validStatements.Count >= 4) {
                if (validStatements[3] is not IfStatementSyntax fourthStatementIf) {
                    log($"[error] no fourthStatementIf");
                    return methodDeclaration;
                }
                
                var strippedIfStatement = StripIfStatement(fourthStatementIf, log);
                if (strippedIfStatement != null) {
                    var remainingStatements = validStatements.Skip(4);
                    var newMethod = SyntaxFactory.MethodDeclaration(methodDeclaration.ReturnType, methodDeclaration.Identifier)
                        .WithModifiers(methodDeclaration.Modifiers)
                        .WithParameterList(methodDeclaration.ParameterList)
                        .WithAttributeLists(methodDeclaration.AttributeLists)
                        .WithBody(MakeRPCMethodBody(SyntaxFactory.List(remainingStatements.Prepend(strippedIfStatement)), log));
                    return newMethod;
                } else {
                    var remainingStatements = validStatements.Skip(4).ToArray();
                    if (fourthStatementIf.Statement.ChildNodes().FirstOrDefault() is ReturnStatementSyntax) {
                        var newMethod = SyntaxFactory.MethodDeclaration(methodDeclaration.ReturnType, methodDeclaration.Identifier)
                            .WithModifiers(methodDeclaration.Modifiers)
                            .WithParameterList(methodDeclaration.ParameterList)
                            .WithAttributeLists(methodDeclaration.AttributeLists)
                            .WithBody(MakeRPCMethodBody(SyntaxFactory.List(remainingStatements), log));
                        return newMethod;
                    } else {
                        var newMethod = SyntaxFactory.MethodDeclaration(methodDeclaration.ReturnType, methodDeclaration.Identifier)
                            .WithModifiers(methodDeclaration.Modifiers)
                            .WithParameterList(methodDeclaration.ParameterList)
                            .WithAttributeLists(methodDeclaration.AttributeLists)
                            .WithBody(MakeRPCMethodBody(TryUnwrapBlock(fourthStatementIf.Statement).Concat(remainingStatements), log));
                        return newMethod;
                    }
                }
            }

            log("[warn] unexpected RPC shape; leaving method unchanged");
            return methodDeclaration;
        }

        private static IfStatementSyntax? StripIfStatement(IfStatementSyntax ifStatementSyntax, Action<string> log) {
            var stripped = StripIfStatementCondition(ifStatementSyntax, keepContent: true, log);
            return stripped;
        }

        private static bool IsExecStageOrRoleCheck(ExpressionSyntax expr) {
            var s = expr.ToString();
            return s.Contains("__rpc_exec_stage")
                || s.Contains(".IsServer") || s.Contains(".IsClient") || s.Contains(".IsHost")
                || s.Contains("NetworkManager.Singleton")
                || s.Contains("base.NetworkManager");
        }

        private static ExpressionSyntax? RemoveGuardTerms(ExpressionSyntax expr) {
            expr = expr is ParenthesizedExpressionSyntax p ? p.Expression : expr;

            if (expr is BinaryExpressionSyntax bin) {
                if (bin.IsKind(SyntaxKind.LogicalAndExpression) || bin.IsKind(SyntaxKind.LogicalOrExpression)) {
                    var left = RemoveGuardTerms(bin.Left);
                    var right = RemoveGuardTerms(bin.Right);

                    if (left == null && right == null) return null;
                    if (left == null) return right;
                    if (right == null) return left;

                    return SyntaxFactory.BinaryExpression(bin.Kind(), left, bin.OperatorToken, right);
                }

                if (IsExecStageOrRoleCheck(expr)) return null;
                return expr;
            }

            return IsExecStageOrRoleCheck(expr) ? null : expr;
        }

        private static IfStatementSyntax? StripIfStatementCondition(IfStatementSyntax ifStatementSyntax, bool keepContent, Action<string> log) {
            var reduced = RemoveGuardTerms(ifStatementSyntax.Condition);

            if (reduced == null) {
                return null;
            }

            return SyntaxFactory.IfStatement(reduced, keepContent ? ifStatementSyntax.Statement : SyntaxFactory.Block(SyntaxFactory.ReturnStatement()));
        }
    }

    public class RemoveCtorMethodCalls : CSharpSyntaxRewriter {
        public override SyntaxNode? VisitExpressionStatement(ExpressionStatementSyntax node) {
            if (node.Expression is InvocationExpressionSyntax invocation &&
                invocation.Expression.ToString().Contains("ctor")) {
                // If the expression is a method call containing "ctor", remove it.
                return null;
            } else {
                // Otherwise, keep the original node.
                return base.VisitExpressionStatement(node);
            }
        }
    }

    public class IndentStatements : CSharpSyntaxRewriter {
        private int _depth = 0;
        private bool _lastTokenHadNewline = true;

        public IndentStatements(int depth) {
            _depth = depth;
        }

        public override SyntaxToken VisitToken(SyntaxToken token) {
            if (_lastTokenHadNewline) {
                token = token.WithLeadingTrivia(token.LeadingTrivia.Concat(Enumerable.Repeat(SyntaxFactory.Tab, _depth)));
            }

            _lastTokenHadNewline = token.TrailingTrivia.Any(t => t.IsKind(SyntaxKind.EndOfLineTrivia));
            return token;
        }
    }
}
