# BatchJob Pricing

## Product Pricing

Batch API inference costs are **60%** of the standard model price, ideal for large-scale tasks with low real-time requirements.

<DocTable
  columns={[
{ title: "Model", width: "20%" },
{ title: "Unit", width: "14%" },
{ title: "Input Price (Cache Hit)", width: "20%" },
{ title: "Input Price (Cache Miss)", width: "20%" },
{ title: "Output Price", width: "13%" },
{ title: "Context Window", width: "13%" },
]}
  rows={[
["kimi-k2.7-code (Batch)", "1M tokens", "$0.114", "$0.57", "$2.40", "262,144 tokens"],
["kimi-k2.6 (Batch)", "1M tokens", "$0.10", "$0.57", "$2.40", "262,144 tokens"],
["kimi-k2.5 (Batch)", "1M tokens", "$0.06", "$0.36", "$1.80", "262,144 tokens"],
]}
/>

Here, 1M = 1,000,000. The prices in the table represent the cost per 1M tokens consumed.
