"use strict";

// Read-only on purpose. There is no login until phase 6, so this page must not be able to change data.
//
// Security: names, addresses and failure reasons come from users and servers. They only ever reach the
// page through textContent, which the browser shows as plain letters and never reads as HTML.

const REFRESH_MILLISECONDS = 10000;
const FETCH_TIMEOUT_MILLISECONDS = 5000;
const GRADE_EXPLANATION = "Grade A means that all five security headers are present. Their values are not assessed.";

let lastSuccessfulUpdate = null;

async function refresh() {
  try {
    const services = await fetchOverview();
    showServices(services);
    showUpdateSucceeded();
  } catch (error) {
    // Old data must never look current, so a failed update has to be seen on the page.
    showUpdateFailed();
  }

  // Started only when this update is finished, so that two updates can never overlap.
  setTimeout(refresh, REFRESH_MILLISECONDS);
}

async function fetchOverview() {
  // The timeout is there because an answer that never comes would otherwise stop all later updates.
  const response = await fetch("/services/overview", {
    cache: "no-store",
    signal: AbortSignal.timeout(FETCH_TIMEOUT_MILLISECONDS)
  });

  if (!response.ok) {
    throw new Error("The API did not answer with success.");
  }

  const services = await response.json();

  if (!Array.isArray(services)) {
    throw new Error("The API did not answer with a list.");
  }

  return services;
}

function showServices(services) {
  const rows = document.getElementById("service-rows");

  while (rows.firstChild) {
    rows.removeChild(rows.firstChild);
  }

  for (let i = 0; i < services.length; i++) {
    rows.appendChild(createRow(services[i]));
  }

  document.getElementById("no-services").hidden = services.length > 0;
}

function createRow(service) {
  const row = document.createElement("tr");

  row.appendChild(createNameCell(service));
  // Plain text in a cell and never a link: the address comes from a user.
  row.appendChild(createTextCell(service.url, "wrap-text"));
  row.appendChild(createStatusCell(service.currentStatus));
  row.appendChild(createStatusCell(service.lastKnownStatus));
  row.appendChild(createTextCell(formatResponseTime(service.responseTimeMs), "no-wrap"));
  row.appendChild(createTextCell(formatTime(service.lastCheckedAt), "no-wrap"));
  row.appendChild(createTextCell(formatFailureReason(service.failureReason), "wrap-text"));
  row.appendChild(createTextCell(formatSecurityGrade(service), "no-wrap"));
  row.appendChild(createTextCell(formatHeaderDetails(service), "wrap-text"));

  return row;
}

function createTextCell(text, className) {
  const cell = document.createElement("td");
  cell.className = className;
  cell.textContent = text;
  return cell;
}

function createNameCell(service) {
  const cell = createTextCell(service.name, "wrap-text");

  // A paused service is not checked, so its status can be old. Without this it would look current.
  if (!service.isEnabled) {
    const paused = document.createElement("span");
    paused.className = "paused";
    paused.textContent = "Paused";
    cell.appendChild(paused);
  }

  return cell;
}

function createStatusCell(status) {
  const cell = document.createElement("td");

  if (status === null) {
    cell.textContent = "-";
    return cell;
  }

  const label = document.createElement("span");
  label.className = "status " + getStatusClass(status);
  label.textContent = status;
  cell.appendChild(label);

  return cell;
}

// The class is picked from a fixed list, so a value from the API never becomes part of the page's code.
function getStatusClass(status) {
  if (status === "UP") {
    return "status-up";
  }

  if (status === "DOWN") {
    return "status-down";
  }

  return "status-unknown";
}

function formatResponseTime(milliseconds) {
  if (milliseconds === null) {
    return "-";
  }

  return milliseconds + " ms";
}

// The API always sends UTC with Z, so the browser can convert it to the owner's local time.
function formatTime(time) {
  if (time === null) {
    return "Never";
  }

  return new Date(time).toLocaleString();
}

function formatFailureReason(reason) {
  if (reason === null) {
    return "";
  }

  return reason;
}

function formatSecurityGrade(service) {
  if (service.securityGrade !== null) {
    return service.securityGrade;
  }

  if (service.securityNotAssessedReason !== null) {
    return "Not assessed";
  }

  return "-";
}

// Shows why there is no grade, or else which headers are missing. An empty text means none are.
function formatHeaderDetails(service) {
  if (service.securityNotAssessedReason !== null) {
    return service.securityNotAssessedReason;
  }

  if (service.missingSecurityHeaders === null) {
    return "-";
  }

  if (service.missingSecurityHeaders === "") {
    return "None";
  }

  return service.missingSecurityHeaders;
}

function showUpdateSucceeded() {
  lastSuccessfulUpdate = new Date();

  document.getElementById("update-time").textContent = "Updated " + lastSuccessfulUpdate.toLocaleString();
  document.getElementById("update-error").hidden = true;
  document.getElementById("table-frame").classList.remove("stale");
}

function showUpdateFailed() {
  const message = document.getElementById("update-error");

  if (lastSuccessfulUpdate === null) {
    message.textContent = "Could not update. No data has been loaded.";
  } else {
    message.textContent = "Could not update. Data from " + lastSuccessfulUpdate.toLocaleString();
  }

  message.hidden = false;
  document.getElementById("table-frame").classList.add("stale");
}

document.getElementById("grade-explanation").textContent = GRADE_EXPLANATION;
refresh();
