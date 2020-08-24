﻿using DevOps.Util.DotNet;
using DevOps.Util.Triage;
using Microsoft.EntityFrameworkCore;
using Octokit;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Text;
using System.Threading.Tasks;

namespace DevOps.Status.Util
{
    public class StatusTestSearchOptions
    {
        public string? Name { get; set; }

        public IQueryable<ModelTestResult> GetModelTestResultsQuery(
            TriageContextUtil triageContextUtil,
            IQueryable<ModelBuild> buildQuery)
        {
            IQueryable<ModelTestResult> query = buildQuery
                .Join(
                    triageContextUtil.Context.ModelTestResults,
                    b => b.Id,
                    t => t.ModelBuildId,
                    (b, t) => t);

            if (!string.IsNullOrEmpty(Name))
            {
                var text = Name.Replace('*', '%');
                text = '%' + text.Trim('%') + '%';
                query = query.Where(t => EF.Functions.Like(t.TestFullName, text));
            }

            return query;
        }

        public string GetUserQueryString()
        {
            var builder = new StringBuilder();
            if (!string.IsNullOrEmpty(Name))
            {
                Append($"name:\"{Name}\"");
            }

            return builder.ToString();

            void Append(string message)
            {
                if (builder.Length != 0)
                {
                    builder.Append(" ");
                }

                builder.Append(message);
            }
        }

        public void Parse(string userQuery)
        {
            if (!userQuery.Contains(":"))
            {
                Name = userQuery.Trim('"');
                return;
            }

            foreach (var tuple in DotNetQueryUtil.TokenizeQueryPairs(userQuery))
            {
                switch (tuple.Name.ToLower())
                {
                    case "name":
                        Name = tuple.Value.Trim('"');
                        break;
                    default:
                        throw new Exception($"Invalid option {tuple.Name}");
                }
            }
        }
    }
}
