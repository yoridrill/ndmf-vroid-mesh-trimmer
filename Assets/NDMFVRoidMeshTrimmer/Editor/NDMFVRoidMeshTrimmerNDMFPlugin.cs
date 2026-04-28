using UnityEngine;
using nadena.dev.ndmf;

[assembly: ExportsPlugin(typeof(NDMFVRoidMeshTrimmerNDMFPlugin))]

public class NDMFVRoidMeshTrimmerNDMFPlugin : Plugin<NDMFVRoidMeshTrimmerNDMFPlugin>
{
    private static readonly string[] VrcQuestToolsPluginCandidates =
    {
        "KRT.VRCQuestTools.Ndmf.VRCQuestToolsPlugin",
        "KRT.VRCQuestTools.Ndmf.AvatarConverterNdmfPlugin",
        "KRT.VRCQuestTools.AvatarConverter.Ndmf.AvatarConverterPlugin",
        "KRT.VRCQuestTools.Ndmf.MaterialConversionNdmfPlugin"
    };

    public override string DisplayName => "NDMF VRoid Mesh Trimmer";

    protected override void Configure()
    {
        ConfigurePhase(BuildPhase.Transforming);
        ConfigurePhase(BuildPhase.Optimizing);
    }

    private void ConfigurePhase(BuildPhase phase)
    {
        var sequence = InPhase(phase);
        foreach (var pluginName in VrcQuestToolsPluginCandidates)
        {
            sequence = sequence.BeforePlugin(pluginName);
        }

        sequence.Run($"Run NDMF VRoid Mesh Trimmer ({phase})", context =>
        {
            var state = context.GetState<NDMFVRoidMeshTrimmerBuildState>();
            if (state.alreadyExecuted)
            {
                return;
            }

            RunTrimmer(context);
            state.alreadyExecuted = true;
        });
    }

    private static void RunTrimmer(BuildContext context)
    {
        GameObject avatarRoot = context.AvatarRootObject;
        if (avatarRoot == null)
        {
            return;
        }

        var trimmers = avatarRoot.GetComponentsInChildren<NDMFVRoidMeshTrimmer>(true);
        foreach (var trimmer in trimmers)
        {
            if (trimmer == null || !trimmer.enabled)
            {
                continue;
            }

            MeshTrimProcessor.ApplyTrim(trimmer);
            TexturePostProcessProcessor.ApplyBuildTimeReplacement(trimmer);
        }
    }

    private class NDMFVRoidMeshTrimmerBuildState
    {
        public bool alreadyExecuted;
    }
}
