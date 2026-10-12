/** Source of "now". Code takes a Clock rather than reading the system clock, so time is controllable in tests. */
export interface Clock {
  readonly now: () => Date
}

/** The one place that reads the system clock. */
export const systemClock: Clock = { now: () => new Date() }
