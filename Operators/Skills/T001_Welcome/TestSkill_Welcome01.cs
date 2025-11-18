namespace Skills.T001_Welcome;

[Guid("4f9eb54f-1b81-4a6b-a842-f80c423e5843")]
internal sealed class TestSkill_Welcome01 : Instance<TestSkill_Welcome01>
{
    [Output(Guid = "bfa820e6-ac48-41de-9303-d07d004744e1")]
    public readonly Slot<Texture2D> ColorBuffer = new();


}