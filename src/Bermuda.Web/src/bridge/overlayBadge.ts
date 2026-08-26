const SIZE = 32

/** Renders a small red circular count badge to a PNG data URL, for use with bermudaHost.window.setOverlay. */
export function renderCountBadge(count: number): string {
  const canvas = document.createElement('canvas')
  canvas.width = SIZE
  canvas.height = SIZE
  const ctx = canvas.getContext('2d')
  if (!ctx) return ''

  ctx.beginPath()
  ctx.arc(SIZE / 2, SIZE / 2, SIZE / 2 - 1, 0, Math.PI * 2)
  ctx.fillStyle = '#dc2626'
  ctx.fill()

  const label = count > 99 ? '99+' : String(count)
  ctx.fillStyle = '#ffffff'
  ctx.font = `bold ${label.length > 2 ? 12 : 18}px sans-serif`
  ctx.textAlign = 'center'
  ctx.textBaseline = 'middle'
  ctx.fillText(label, SIZE / 2, SIZE / 2 + 1)

  return canvas.toDataURL('image/png')
}
