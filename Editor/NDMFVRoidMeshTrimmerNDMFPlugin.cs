using nadena.dev.ndmf;

[assembly: ExportsPlugin(typeof(NDMFVRoidMeshTrimmerNDMFPlugin))]

public class NDMFVRoidMeshTrimmerNDMFPlugin : Plugin<NDMFVRoidMeshTrimmerNDMFPlugin>
{
    public override string DisplayName => "NDMF VRoid Mesh Trimmer";

    protected override void Configure()
    {
        var sequence = InPhase(BuildPhase.Transforming)
            .BeforePlugin("KRT.VRCQuestTools.Ndmf.VRCQuestToolsPlugin")
            .BeforePlugin("KRT.VRCQuestTools.Ndmf.AvatarConverterNdmfPlugin")
            .BeforePlugin("KRT.VRCQuestTools.AvatarConverter.Ndmf.AvatarConverterPlugin")
            .BeforePlugin("KRT.VRCQuestTools.Ndmf.MaterialConversionNdmfPlugin");

        sequence.Run("Run NDMF VRoid Mesh Trimmer", context =>
        {
            var avatarRoot = context.AvatarRootObject;
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
        });
    }
}
