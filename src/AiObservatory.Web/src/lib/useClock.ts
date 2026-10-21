import { createContext, useContext } from 'react'
import { systemClock, type Clock } from './clock'

/** Defaults to the system clock; tests provide a fixed one with ClockContext.Provider. */
export const ClockContext = createContext<Clock>(systemClock)

export function useClock(): Clock {
  return useContext(ClockContext)
}
