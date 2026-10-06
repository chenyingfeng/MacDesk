using System;
namespace StageManager.Services;
internal static class CaptureCleanup
{
    // A closed target may already have lost its CompositionTarget. Disposal still has to run.
    internal static void Run(Action detach,Action dispose,Action<Exception> report) {
        try {detach();} catch(Exception ex) {report(ex);}
        finally {try {dispose();} catch(Exception ex) {report(ex);}}
    }
}
