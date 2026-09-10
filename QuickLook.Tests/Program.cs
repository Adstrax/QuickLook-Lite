// Copyright © 2017-2026 QL-Win Contributors
//
// This file is part of QuickLookNext program.
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU General Public License for more details.
//
// You should have received a copy of the GNU General Public License
// along with this program.  If not, see <http://www.gnu.org/licenses/>.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace QuickLook.Tests;

/// <summary>
/// v3.31.0: runs every public void method of every class whose name ends with
/// "Tests". Per-test setup/teardown goes through <see cref="ITestFixture"/>.
/// </summary>
internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        var filter = args.FirstOrDefault(a => !a.StartsWith("-", StringComparison.Ordinal));
        var verbose = args.Contains("-v");

        var fixtures = typeof(Program).Assembly
            .GetTypes()
            .Where(t => t.IsClass && !t.IsAbstract && t.Name.EndsWith("Tests", StringComparison.Ordinal))
            .OrderBy(t => t.Name, StringComparer.Ordinal)
            .ToList();

        var passed = 0;
        var failures = new List<string>();

        foreach (var fixtureType in fixtures)
        {
            var tests = fixtureType
                .GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .Where(m => m.DeclaringType == fixtureType &&
                            m.ReturnType == typeof(void) &&
                            m.GetParameters().Length == 0 &&
                            m.Name != nameof(ITestFixture.Setup) &&
                            m.Name != nameof(ITestFixture.Teardown))
                .OrderBy(m => m.Name, StringComparer.Ordinal)
                .ToList();

            foreach (var test in tests)
            {
                var name = $"{fixtureType.Name}.{test.Name}";
                if (filter != null && name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0)
                    continue;

                var fixture = Activator.CreateInstance(fixtureType);
                var scope = fixture as ITestFixture;

                try
                {
                    scope?.Setup();
                    test.Invoke(fixture, null);
                    passed++;
                    Console.WriteLine($"  PASS  {name}");
                }
                catch (TargetInvocationException e)
                {
                    var reason = (e.InnerException ?? e).Message;
                    failures.Add($"{name}: {reason}");
                    Console.WriteLine($"  FAIL  {name}");
                    Console.WriteLine($"        {reason}");
                }
                catch (Exception e)
                {
                    failures.Add($"{name}: {e.Message}");
                    Console.WriteLine($"  FAIL  {name}");
                    Console.WriteLine($"        {e.Message}");
                }
                finally
                {
                    try
                    {
                        scope?.Teardown();
                    }
                    catch (Exception e)
                    {
                        if (verbose)
                            Console.WriteLine($"        (teardown: {e.Message})");
                    }
                }
            }
        }

        Console.WriteLine();
        Console.WriteLine($"{passed} passed, {failures.Count} failed");

        if (failures.Count > 0)
        {
            Console.WriteLine();
            foreach (var failure in failures)
                Console.WriteLine($"FAILED {failure}");
        }

        return failures.Count == 0 ? 0 : 1;
    }
}
