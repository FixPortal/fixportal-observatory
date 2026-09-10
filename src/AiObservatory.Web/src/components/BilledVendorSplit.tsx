import { Bar, BarChart, Cell, LabelList, ResponsiveContainer, Tooltip, XAxis, YAxis } from 'recharts'
import type { ValueType } from 'recharts/types/component/DefaultTooltipContent'
import { gbp } from '../lib/currency'
import type { BilledReporting } from '../api/client'
import { providerColor } from '../theme/providerColors'

const TEXT_MUTED = 'var(--text-muted)'

interface Props {
  data: BilledReporting['vendorSeries']
}

export default function BilledVendorSplit({ data }: Props) {
  return (
    <ResponsiveContainer width="100%" height={200}>
      <BarChart data={data} layout="vertical" margin={{ right: 56 }}>
        <XAxis type="number" tick={{ fontSize: 10, fill: TEXT_MUTED }} tickFormatter={(value: number) => gbp(value)} />
        <YAxis type="category" dataKey="name" width={100} tick={{ fontSize: 10, fill: TEXT_MUTED }} />
        <Tooltip
          contentStyle={{ background: 'var(--card-bg)', border: '1px solid var(--border)', borderRadius: 'var(--r-chip)', color: 'var(--text)' }}
          labelStyle={{ color: 'var(--text)' }}
          itemStyle={{ color: TEXT_MUTED }}
          formatter={(value: ValueType | undefined) => gbp(Number(Array.isArray(value) ? value[0] : value ?? 0))}
        />
        <Bar dataKey="amountGbp" name="Billed spend">
          {data.map(entry => <Cell key={entry.vendorId} fill={entry.amountGbp < 0 ? 'var(--bad-border)' : providerColor(entry.provider ?? 'other')} />)}
          <LabelList dataKey="amountGbp" position="right" formatter={value => gbp(Number(value ?? 0))} fill={TEXT_MUTED} fontSize={10} />
        </Bar>
      </BarChart>
    </ResponsiveContainer>
  )
}
