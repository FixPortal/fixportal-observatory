import { test, expect } from 'vitest'
import { formatActiveTime, formatTurnaround } from './duration'

test('formats under an hour as minutes only', () => {
  expect(formatActiveTime(45 * 60)).toBe('45m')
})

test('formats over an hour as hours and minutes', () => {
  expect(formatActiveTime(6 * 3600 + 40 * 60)).toBe('6h 40m')
})

test('rounds to the nearest minute', () => {
  expect(formatActiveTime(89)).toBe('1m') // 89s rounds to 1m, not 0m
})

test('formats zero seconds as 0m', () => {
  expect(formatActiveTime(0)).toBe('0m')
})

test('formats an exact hour with no leftover minutes', () => {
  expect(formatActiveTime(3600)).toBe('1h 0m')
})

test.each([
  [0.3, '18m'],
  [1.25, '1.3h'],
  [47.9, '47.9h'],
  [72, '3.0d'],
])('formats a %s-hour review turnaround as %s', (hours, expected) => {
  expect(formatTurnaround(hours)).toBe(expected)
})

// An agent that answers in seconds must not render as "0m", which reads as "never ran".
test('floors a sub-minute turnaround at one minute', () => {
  expect(formatTurnaround(0.001)).toBe('1m')
})
