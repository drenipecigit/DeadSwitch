"use strict";

// Read-only on purpose. There is no login until phase 6, so this page must not be able to change data.
//
// Security: names, addresses and failure reasons come from users and servers. They only ever reach the
// page through textContent or title, which the browser shows as plain letters and never reads as HTML.
//
// The decisions (status, order, grade, texts and times) are in dashboard-logic.js, which is loaded first.
// This file only builds the page from what those functions return.

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
  const list = document.getElementById("service-cards");

  while (list.firstChild) {
    list.removeChild(list.firstChild);
  }

  const sorted = sortServices(services);

  for (let i = 0; i < sorted.length; i++) {
    list.appendChild(createCard(sorted[i]));
  }

  document.getElementById("summary").textContent = formatSummary(services);
  document.getElementById("no-services").hidden = services.length > 0;
}

function createCard(service) {
  const card = document.createElement("li");
  card.className = "card";

  const badge = getStatusBadge(service.currentStatus, service.lastKnownStatus);
  card.appendChild(createTopRow(service, badge));
  card.appendChild(createDetailRow(service, badge));

  return card;
}

// Status, name, address and grade: what the owner needs to see at a glance.
function createTopRow(service, badge) {
  const row = document.createElement("div");
  row.className = "card-top";

  row.appendChild(createText("span", "status " + badge.className, badge.text));
  row.appendChild(createText("span", "card-name", service.name));

  // A paused service is not checked, so its status can be old. Without this it would look current.
  if (!service.isEnabled) {
    row.appendChild(createText("span", "paused", "Paused"));
  }

  // Plain text and never a link: the address comes from a user.
  row.appendChild(createText("span", "card-url", service.url));

  const grade = getGradeBox(service.securityGrade);
  const gradeBox = createText("span", "grade " + grade.className, grade.text);
  gradeBox.title = "Security grade";
  row.appendChild(gradeBox);

  return row;
}

// Smaller text below. A field with nothing to say is left out, so that a healthy card stays short.
function createDetailRow(service, badge) {
  const row = document.createElement("div");
  row.className = "card-details";

  if (badge.detail !== null) {
    appendDetail(row, badge.detail);
  }

  appendDetail(row, formatLastCheck(service.lastCheckedAt));

  const responseTime = formatResponseTime(service.responseTimeMs);
  if (responseTime !== null) {
    appendDetail(row, responseTime);
  }

  if (service.failureReason !== null) {
    appendDetail(row, service.failureReason);
  }

  const certificate = getCertificate(service);
  if (certificate !== null) {
    const details = appendDetail(row, certificate.text);
    // Set as a property, which the browser keeps as plain text and shows as a tooltip.
    details.title = certificate.title;
  }

  const headerDetails = getHeaderDetails(service);
  if (headerDetails !== null) {
    appendDetail(row, headerDetails);
  }

  // Plain text and never a link: the host was chosen by the monitored site.
  const redirect = getRedirectText(service);
  if (redirect !== null) {
    appendDetail(row, redirect);
  }

  return row;
}

function appendDetail(row, text) {
  const detail = createText("span", "detail", text);
  row.appendChild(detail);
  return detail;
}

// The only way text reaches the page. The class name is always one of our own.
function createText(tagName, className, text) {
  const element = document.createElement(tagName);
  element.className = className;
  element.textContent = text;
  return element;
}

function showUpdateSucceeded() {
  lastSuccessfulUpdate = new Date();

  document.getElementById("update-time").textContent = "Updated " + formatLocalTime(lastSuccessfulUpdate);
  document.getElementById("update-error").hidden = true;
  document.getElementById("service-area").classList.remove("stale");
}

function showUpdateFailed() {
  const message = document.getElementById("update-error");

  if (lastSuccessfulUpdate === null) {
    message.textContent = "Could not update. No data has been loaded.";
  } else {
    message.textContent = "Could not update. Data from " + formatLocalTime(lastSuccessfulUpdate);
  }

  message.hidden = false;
  document.getElementById("service-area").classList.add("stale");
}

document.getElementById("grade-explanation").textContent = GRADE_EXPLANATION;
refresh();
