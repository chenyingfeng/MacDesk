using System;
using System.Linq;
using System.Collections.Generic;

namespace StageManager.Services;
internal static class FocusHandoff
{
    internal static bool MayFinish(IntPtr before, IntPtr now, IReadOnlyCollection<IntPtr> targetMembers,
        long inputBefore, long inputNow, long navigationBefore, long navigationNow, bool selectorActive) =>
        !selectorActive && inputBefore == inputNow && navigationBefore == navigationNow
        && (before == now || targetMembers.Contains(now));
}
