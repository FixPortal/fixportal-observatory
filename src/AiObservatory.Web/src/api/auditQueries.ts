import { useEffect, useState } from 'react'
import { useInfiniteQuery, useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import {
  cancelAuditRun,
  createAuditRun,
  getAuditCapabilities,
  getAuditFindings,
  getAuditReadiness,
  getAuditRun,
  getAuditRuns,
  proceedAuditRun,
  replayAuditRun,
  verifyAuditPresentation,
} from './auditClient'
import type { AuditRun, AuditScope } from './auditTypes'

export const isActiveAudit = (run: AuditRun) =>
  ['queued', 'collecting', 'collectingEvidence', 'snapshotting', 'verifying', 'executing'].includes(
    run.state,
  )
function useVisible() {
  const [visible, setVisible] = useState(() => !document.hidden)
  useEffect(() => {
    const changed = () => {
      setVisible(!document.hidden)
    }
    document.addEventListener('visibilitychange', changed)
    return () => {
      document.removeEventListener('visibilitychange', changed)
    }
  }, [])
  return visible
}
export const useAuditCapabilities = () =>
  useQuery({
    queryKey: ['audit-capabilities'],
    queryFn: getAuditCapabilities,
    retry: false,
    staleTime: 60_000,
  })
export const useAuditReadiness = (scope: AuditScope, enabled = true) =>
  useQuery({
    queryKey: [
      'audit-readiness',
      scope.from,
      scope.to,
      scope.sources.map((s) => s.sourceId).sort(),
    ],
    queryFn: () => getAuditReadiness(scope),
    enabled,
    retry: false,
  })
export function useAuditRuns(cursor: string | null = null) {
  const visible = useVisible()
  return useInfiniteQuery({
    queryKey: ['audit-runs', cursor],
    initialPageParam: cursor,
    queryFn: ({ pageParam }) => getAuditRuns(pageParam),
    getNextPageParam: (page) => page.nextCursor,
    refetchInterval: (query) =>
      visible && query.state.data?.pages.some((p) => p.items.some(isActiveAudit)) ? 2000 : false,
    refetchIntervalInBackground: false,
    retry: false,
  })
}
export function useAuditRun(id: string) {
  const visible = useVisible()
  return useQuery({
    queryKey: ['audit-run', id],
    queryFn: () => getAuditRun(id),
    refetchInterval: (query) =>
      visible && query.state.data && isActiveAudit(query.state.data) ? 2000 : false,
    refetchIntervalInBackground: false,
    retry: false,
  })
}
export const useAuditFindings = (id: string, enabled: boolean) =>
  useInfiniteQuery({
    queryKey: ['audit-findings', id],
    initialPageParam: null as string | null,
    queryFn: ({ pageParam }) => getAuditFindings(id, pageParam),
    getNextPageParam: (page) => page.nextCursor,
    enabled,
    retry: false,
  })
export function useAuditActions() {
  const client = useQueryClient()
  const updated = async () => {
    await Promise.all([
      client.invalidateQueries({ queryKey: ['audit-runs'] }),
      client.invalidateQueries({ queryKey: ['audit-run'] }),
    ])
  }
  return {
    create: useMutation({ mutationFn: createAuditRun, onSuccess: updated }),
    cancel: useMutation({ mutationFn: cancelAuditRun, onSuccess: updated }),
    proceed: useMutation({ mutationFn: proceedAuditRun, onSuccess: updated }),
    replay: useMutation({ mutationFn: replayAuditRun, onSuccess: updated }),
    probe: useMutation({
      mutationFn: (id: string) => verifyAuditPresentation(id),
      onSuccess: updated,
    }),
  }
}
