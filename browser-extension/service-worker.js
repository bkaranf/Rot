import { sendCurrentTabToRot } from "./extension.js";

chrome.action.onClicked.addListener((tab) => {
  void sendCurrentTabToRot(chrome, tab);
});
