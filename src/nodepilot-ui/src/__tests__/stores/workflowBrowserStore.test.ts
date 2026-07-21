import { describe, it, expect, beforeEach } from 'vitest';
import { useWorkflowBrowserStore } from '../../stores/workflowBrowserStore';

const initialState = useWorkflowBrowserStore.getState();

describe('useWorkflowBrowserStore', () => {
  beforeEach(() => {
    useWorkflowBrowserStore.setState(initialState, true);
  });

  it('defaults_folderViewMode_andEmptyCollapsedMap', () => {
    const s = useWorkflowBrowserStore.getState();
    expect(s.viewMode).toBe('folder');
    expect(s.collapsedFolders).toEqual({});
  });

  it('defaults_infoCardHeight_to320', () => {
    expect(useWorkflowBrowserStore.getState().infoCardHeight).toBe(320);
  });

  it('setInfoCardHeight_updatesValue', () => {
    useWorkflowBrowserStore.getState().setInfoCardHeight(320);
    expect(useWorkflowBrowserStore.getState().infoCardHeight).toBe(320);
  });

  it('setViewMode_switchesToFolder', () => {
    useWorkflowBrowserStore.getState().setViewMode('folder');
    expect(useWorkflowBrowserStore.getState().viewMode).toBe('folder');

    useWorkflowBrowserStore.getState().setViewMode('trigger');
    expect(useWorkflowBrowserStore.getState().viewMode).toBe('trigger');
  });

  it('toggleFolder_addsTrueWhenAbsent', () => {
    useWorkflowBrowserStore.getState().toggleFolder('folder-1');
    expect(useWorkflowBrowserStore.getState().collapsedFolders['folder-1']).toBe(true);
  });

  it('toggleFolder_alternatesValueOnRepeatCall', () => {
    const store = useWorkflowBrowserStore.getState();
    store.toggleFolder('folder-1');
    store.toggleFolder('folder-1');
    expect(useWorkflowBrowserStore.getState().collapsedFolders['folder-1']).toBe(false);

    store.toggleFolder('folder-1');
    expect(useWorkflowBrowserStore.getState().collapsedFolders['folder-1']).toBe(true);
  });

  it('toggleFolder_independentPerFolderId', () => {
    const store = useWorkflowBrowserStore.getState();
    store.toggleFolder('a');
    store.toggleFolder('b');
    store.toggleFolder('a'); // a back to false

    const state = useWorkflowBrowserStore.getState().collapsedFolders;
    expect(state.a).toBe(false);
    expect(state.b).toBe(true);
  });
});
