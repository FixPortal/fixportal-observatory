import { useQuery } from '@tanstack/react-query'
import { getSuggestions, type Suggestion, type SuggestionsResponse } from './client'

// Open suggestions plus the rules that could not evaluate. The status filter is
// part of the request, so every suggestion the UI receives is actionable now --
// dismissed and snoozed rows stay server-side until their condition reopens.
export function useSuggestions(): {
  suggestions: Suggestion[]
  notEvaluated: SuggestionsResponse['notEvaluated']
  isLoading: boolean
  isError: boolean
} {
  const { data, isPending, isError } = useQuery({
    queryKey: ['suggestions'],
    queryFn: getSuggestions,
  })
  return {
    suggestions: data?.suggestions ?? [],
    notEvaluated: data?.notEvaluated ?? [],
    isLoading: isPending,
    isError,
  }
}
