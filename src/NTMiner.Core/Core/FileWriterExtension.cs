﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using NTMiner.Core.Kernels;

namespace NTMiner.Core {
    public static class FileWriterExtension {
        public class ParameterNames {
            // 根据这个判断是否换成过期
            internal string Body = string.Empty;
            internal readonly HashSet<string> Names = new HashSet<string>();
        }

        private static readonly Dictionary<Guid, ParameterNames> _parameterNameDic = new Dictionary<Guid, ParameterNames>();

        private static ParameterNames GetParameterNames(IFragmentWriter writer) {
            if (string.IsNullOrEmpty(writer.Body)) {
                return new ParameterNames {
                    Body = writer.Body
                };
            }
            if (_parameterNameDic.TryGetValue(writer.GetId(), out ParameterNames parameterNames) 
                && parameterNames.Body == writer.Body) {
                return parameterNames;
            }
            else {
                if (parameterNames != null) {
                    parameterNames.Body = writer.Body;
                }
                else {
                    parameterNames = new ParameterNames {
                        Body = writer.Body
                    };
                    _parameterNameDic.Add(writer.GetId(), parameterNames);
                }
                parameterNames.Names.Clear();
                const string pattern = @"\{(\w+)\}";
                var matches = Regex.Matches(writer.Body, pattern);
                foreach (Match match in matches) {
                    parameterNames.Names.Add(match.Groups[1].Value);
                }
                return parameterNames;
            }
        }

        private static bool IsMatch(IFragmentWriter writer, IMineContext mineContext, out ParameterNames parameterNames) {
            parameterNames = GetParameterNames(writer);
            if (string.IsNullOrEmpty(writer.Body)) {
                return false;
            }
            if (parameterNames.Names.Count == 0) {
                return true;
            }
            foreach (var name in parameterNames.Names) {
                if (!mineContext.Parameters.ContainsKey(name)) {
                    return false;
                }
            }
            return true;
        }

        public static void Execute(this IFileWriter fileWriter, IMineContext mineContext) {
            string content = BuildFragment(fileWriter, mineContext);
            if (!string.IsNullOrEmpty(content)) {
                string fileFullName = Path.Combine(mineContext.Kernel.GetKernelDirFullName(), fileWriter.FileUrl);
                File.WriteAllText(fileFullName, content);
            }
        }

        public static string BuildFragment(this IFragmentWriter writer, IMineContext mineContext) {
            if (!IsMatch(writer, mineContext, out ParameterNames parameterNames)) {
                return string.Empty;
            }
            string content = writer.Body;
            foreach (var parameterName in parameterNames.Names) {
                content = content.Replace($"{{{parameterName}}}", mineContext.Parameters[parameterName]);
            }
            return content;
        }
    }
}
