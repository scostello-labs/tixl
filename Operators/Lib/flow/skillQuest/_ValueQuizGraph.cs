using System;
using T3.Core.DataTypes;
using T3.Core.Operator;
using T3.Core.Operator.Attributes;
using T3.Core.Operator.Slots;
using System.Runtime.InteropServices;

namespace Lib.flow.skillQuest{
    [Guid("64b2f2d5-0261-4f22-b30f-1f3a1421fe2e")]
    internal sealed class _ValueQuizGraph : Instance<_ValueQuizGraph>
    {
        [Output(Guid = "88844b1e-255e-4783-a906-239fefe38afd")]
        public readonly Slot<Texture2D> ColorBuffer = new Slot<Texture2D>();


        [Input(Guid = "afcdab0a-b859-4137-82de-3fc20f306d57")]
        public readonly InputSlot<float> Value = new InputSlot<float>();

    }
}

