using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

[InitializeOnLoad]
public static class NDMFVRoidMeshTrimmerLifecycleHook
{
    private static readonly HashSet<int> Queued = new HashSet<int>();

    static NDMFVRoidMeshTrimmerLifecycleHook()
    {
        ObjectFactory.componentWasAdded += OnComponentWasAdded;
        Undo.postprocessModifications += OnPostprocessModifications;
    }

    private static void OnComponentWasAdded(Component component)
    {
        var trimmer = component as NDMFVRoidMeshTrimmer;
        if (trimmer == null) return;
        QueueDetect(trimmer);
    }

    private static UndoPropertyModification[] OnPostprocessModifications(UndoPropertyModification[] modifications)
    {
        for (int i = 0; i < modifications.Length; i++)
        {
            var target = modifications[i].currentValue.target as NDMFVRoidMeshTrimmer;
            if (target == null) continue;
            if (target.targets == null || target.targets.Count == 0)
            {
                QueueDetect(target);
            }
        }
        return modifications;
    }

    private static void QueueDetect(NDMFVRoidMeshTrimmer trimmer)
    {
        if (trimmer == null) return;
        int id = trimmer.GetInstanceID();
        if (!Queued.Add(id)) return;

        EditorApplication.delayCall += () =>
        {
            Queued.Remove(id);
            if (trimmer == null) return;
            NDMFVRoidMeshTrimmerEditor.EnsureAutoDetectedTargets(trimmer, true);
            EditorUtility.SetDirty(trimmer);
        };
    }
}
