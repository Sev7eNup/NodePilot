import type { DragEvent } from 'react';

export const VARIABLE_DRAG_MIME = 'application/nodepilot-variable';

export function setVariableDragData(event: DragEvent, expression: string): void {
  event.dataTransfer.effectAllowed = 'copy';
  event.dataTransfer.setData(VARIABLE_DRAG_MIME, expression);
  event.dataTransfer.setData('text/plain', expression);
}

export function readDraggedVariableExpression(event: DragEvent): string {
  return event.dataTransfer.getData(VARIABLE_DRAG_MIME)
    || event.dataTransfer.getData('text/plain');
}

export function hasDraggedVariableExpression(event: DragEvent): boolean {
  return Array.from(event.dataTransfer.types).includes(VARIABLE_DRAG_MIME)
    || Array.from(event.dataTransfer.types).includes('text/plain');
}
