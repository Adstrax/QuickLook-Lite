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

namespace QuickLook.Tests;

/// <summary>
/// Minimal assertion helpers; see <c>QuickLook.Tests.csproj</c> for why the
/// suite does not depend on a test framework.
/// </summary>
internal static class Assert
{
    internal static void True(bool condition, string message)
    {
        if (!condition)
            throw new AssertionException($"Expected true: {message}");
    }

    internal static void False(bool condition, string message)
    {
        if (condition)
            throw new AssertionException($"Expected false: {message}");
    }

    internal static void Equal<T>(T expected, T actual, string message)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new AssertionException($"{message}: expected <{expected}>, actual <{actual}>");
    }

    internal static void NotNull(object value, string message)
    {
        if (value is null)
            throw new AssertionException($"Expected not null: {message}");
    }

    internal static void Null(object value, string message)
    {
        if (value != null)
            throw new AssertionException($"Expected null: {message}");
    }

    internal static void Throws<TException>(Action action, string message) where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }
        catch (Exception e)
        {
            throw new AssertionException(
                $"{message}: expected {typeof(TException).Name}, actual {e.GetType().Name}");
        }

        throw new AssertionException($"{message}: expected {typeof(TException).Name}, nothing was thrown");
    }
}

internal sealed class AssertionException : Exception
{
    internal AssertionException(string message) : base(message)
    {
    }
}
