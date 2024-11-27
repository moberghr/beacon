using System.Linq.Expressions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using MudBlazor;

namespace Semantico.UI.Components;

public static class MudExtensions
{
    public class FormTextField<TValue> : ComponentBase
    {
        [Parameter]
        public TValue? Value { get; set; }

        [Parameter] 
        public EventCallback<TValue> ValueChanged { get; set; }

        [Parameter] 
        public Expression<Func<TValue>>? ValueExpression { get; set; }

        [Parameter] 
        public string Label { get; set; }

        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            builder.OpenComponent<MudTextField<TValue>>(0);
            builder.AddAttribute(1, "Variant", Variant.Outlined);
            builder.AddAttribute(2, "Margin", Margin.Dense);
            builder.AddAttribute(3, "Label", this.Label);
            builder.AddAttribute(4, "Value", this.Value);
            builder.AddAttribute(5, "ValueChanged", EventCallback.Factory.Create(this, this.ValueChanged));
            builder.CloseComponent();
        }
    }
}